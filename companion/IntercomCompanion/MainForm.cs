using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using IntercomCompanion.Audio;
using IntercomCompanion.Core;
using IntercomCompanion.Dialogs;
using IntercomCompanion.Views;
using IntercomCompanion.Views.Settings;

namespace IntercomCompanion;

/// <summary>The window controller. It owns node, audio, session, timers and
/// event wiring only; every pixel of layout lives in the views under
/// <c>Views/</c> and <c>Dialogs/</c>.</summary>
internal sealed class MainForm : Form
{
    private readonly CompanionSettings settings = CompanionSettings.Load();
    private readonly KnownDevicesStore knownDevices = KnownDevicesStore.Load();
    private readonly Dictionary<uint, DeviceConfiguration> configurations = [];
    private readonly Dictionary<uint, DateTimeOffset> rememberedAt = [];

    private readonly IdentityStrip identityStrip = new();
    private readonly NowBar nowBar = new();
    private readonly TalkView talkView = new();
    private readonly SettingsView settingsView = new();
    private readonly AppStatusBar statusBar = new();
    private readonly ShellView shell;

    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource otaStopping = new();
    private readonly HashSet<uint> queuedOtaDevices = [];
    private readonly Dictionary<uint, (string State, Color Color, int Progress, string Detail)> otaProgress = [];

    private IntercomNode? node;
    private AudioEngine? audio;
    private ReceiveSession? receiveSession;
    private GlobalPttHotkeys? globalHotkeys;
    private NotifyIcon? trayIcon;

    private IReadOnlyList<Peer> displayPeers = [];
    private OtaPackage? otaPackage;
    private bool spaceHeld;
    private bool otaUpdating;
    private bool allowExit;
    private string? degraded;
    private DateTimeOffset receivingSince = DateTimeOffset.UtcNow;
    private long lastLogLength = -1;

    public MainForm()
    {
        Text = "Wi-Fi Intercom Companion";
        Icon = BrandAssets.AppIcon;
        ShowIcon = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = UiStyles.SecondaryFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(1024, 700);
        BackColor = UiStyles.Surface;
        DoubleBuffered = true;
        KeyPreview = true;
        AcceptButton = null;

        shell = new ShellView(identityStrip, nowBar, talkView, settingsView, statusBar);
        Controls.Add(shell);

        WireEvents();

        Shown += OnShown;
        FormClosing += OnFormClosing;
        refreshTimer.Tick += (_, _) => RefreshPeers();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        talkView.SetBroadcastScope(settings.BroadcastTargetLabel);
        settingsView.Group.SetCompanionId(settings.NodeId);
        settingsView.Identity.SetCompanionId(settings.NodeId);
        settingsView.Identity.SetAlias(settings.Alias);
        settingsView.Identity.SetWindowOptions(settings.RunInNotificationArea, settings.StartWithWindows);
        RefreshGroupPage();
        UpdateIdentityStrip();
        UpdateNowBar();
    }

    // ---- Event wiring ------------------------------------------------------

    private void WireEvents()
    {
        identityStrip.ChangeAudioClicked += (_, _) => OpenSettings("Identity");
        identityStrip.SettingsClicked += (_, _) => OpenSettings("USB");
        identityStrip.LocalMuteToggled += (_, muted) => audio?.SetLocalPlaybackMuted(muted);
        identityStrip.BroadcastTargetSelected += ChangeBroadcastTarget;
        identityStrip.GroupSettingsClicked += (_, _) => OpenSettings("Group");

        BindPtt(talkView.Broadcast, () => receiveSession?.PressBroadcast());
        BindPtt(talkView.Reply, () => receiveSession?.PressReply());

        talkView.Grid.TalkPressed += peer => { if (!otaUpdating) receiveSession?.PressSelected(peer); };
        talkView.Grid.TalkReleased += () => receiveSession?.ReleasePtt();
        talkView.Grid.SilenceClicked += async peer => await ToggleSoftMuteAsync(peer);
        talkView.Grid.MoreClicked += ShowDeviceMenu;
        talkView.Grid.VolumeCommitted += async (peer, value) => await CommitVolumeAsync(peer, value);
        talkView.Grid.BroadcastTargetClicked += ChangeBroadcastTarget;
        talkView.OtherGroups.JoinClicked += code => { node?.JoinGroup(code); RefreshPeers(); };

        talkView.KnownDevices.RemoveClicked += RemoveKnownDevice;
        talkView.KnownDevices.RemoveAllClicked += RemoveAllOffline;
        talkView.Activity.OpenRecordingsClicked += (_, _) => OpenRecordingsFolder();
        talkView.Activity.DiagnosticsClicked += (_, _) => OpenSettings("Diagnostics");

        settingsView.PageShown += OnSettingsPageShown;
        settingsView.BackToTalkClicked += () => shell.ShowTalk();
        settingsView.Usb.RescanClicked += async (_, _) => await RescanUsbAsync();
        settingsView.Usb.SendClicked += async (_, _) => await ApplyUsbWifiAsync();
        settingsView.Group.SetTargetClicked += ChangeBroadcastTarget;
        settingsView.Group.JoinClicked += (code, name) => { node?.JoinGroup(code, name); RefreshPeers(); };
        settingsView.Group.LeaveClicked += code => { node?.LeaveGroup(code); RefreshPeers(); };
        settingsView.Group.RenameClicked += RenameGroup;
        settingsView.Group.MoveClicked += async () => await MoveDevicesAsync();
        settingsView.Firmware.ChoosePackageClicked += (_, _) => ChooseOtaPackage();
        settingsView.Firmware.AddAllClicked += (_, _) => { foreach (var peer in displayPeers.Where(peer => peer.IsOtaEligible)) queuedOtaDevices.Add(peer.NodeId); RefreshOtaQueue(); };
        settingsView.Firmware.StartQueueClicked += async (_, _) => await RunOtaQueueAsync();
        settingsView.Firmware.RowActionClicked += nodeId => { if (!otaUpdating) { queuedOtaDevices.Remove(nodeId); otaProgress.Remove(nodeId); RefreshOtaQueue(); } };
        settingsView.Identity.SaveClicked += (_, _) => SaveIdentitySettings();
        settingsView.Diagnostics.CopyLogClicked += (_, _) => { try { Clipboard.SetText(ReadDiagnosticLog()); } catch { } };
        settingsView.Diagnostics.SaveLogClicked += (_, _) => SaveDiagnosticLog();
    }

    private void OpenSettings(string page)
    {
        settingsView.Show(page);
        shell.ShowSettings();
    }

    // ---- Lifecycle ---------------------------------------------------------

    private void OnShown(object? sender, EventArgs e)
    {
        try
        {
            settings.Save();
            node = new IntercomNode(settings);
            node.PeersChanged += (_, _) => PostToUi(RefreshPeers);
            node.Diagnostic += message => PostToUi(() => statusBar.SetMessage($"Discovery: {message}"));
            node.OtaStatusReceived += (_, args) => PostToUi(() => OnOtaStatus(args));
            node.Start();
            statusBar.SetMessage($"Discovery: listening on {DiscoveryEndpoint}");
            try
            {
                globalHotkeys = new GlobalPttHotkeys();
                globalHotkeys.BroadcastPressed += () => PostToUi(() => receiveSession?.PressBroadcast());
                globalHotkeys.ReplyPressed += () => PostToUi(() => receiveSession?.PressReply());
                globalHotkeys.Released += () => PostToUi(() => receiveSession?.ReleasePtt());
            }
            catch (Exception exception)
            {
                RecordActivity($"Global hotkeys unavailable: {exception.Message}");
            }
        }
        catch (SocketException exception)
        {
            ShowDegraded("NETWORK UNAVAILABLE", "Network unavailable", $"Discovery unavailable: {exception.SocketErrorCode}", UiStyles.Red, UiStyles.RedTint);
            return;
        }

        try
        {
            PopulateAudioDevices();
            StartAudioEngine();
            talkView.Broadcast.Enabled = talkView.Reply.Enabled = true;
            UpdateNowBar();
        }
        catch (Exception exception)
        {
            ShowDegraded("AUDIO UNAVAILABLE", "Audio unavailable", exception.Message, UiStyles.Amber, UiStyles.AmberTint);
        }

        _ = RescanUsbAsync();
        _ = RefreshSsidsAsync();
        refreshTimer.Start();
        EnableTray();
        RefreshPeers();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (KeepRunningInTray(e)) return;
        refreshTimer.Stop();
        otaStopping.Cancel();
        globalHotkeys?.Dispose();
        globalHotkeys = null;
        receiveSession?.Stop();
        audio?.Dispose();
        node?.Stop();
    }

    private void PostToUi(Action action)
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
    }

    // Esc returns to Talk from any settings page — except while a combo drop-down
    // is open, where Esc must close the drop-down first (adjustments/01-back-to-talk.md).
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && settingsView.Visible)
        {
            var focused = ActiveControl;
            while (focused is ContainerControl { ActiveControl: { } inner }) focused = inner;
            if (focused is not ComboBox { DroppedDown: true })
            {
                shell.ShowTalk();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- Peer refresh ------------------------------------------------------

    private void RefreshPeers()
    {
        if (node is null || IsDisposed) return;
        var peers = node.Peers.OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
        displayPeers = peers;

        foreach (var peer in peers)
        {
            if (!rememberedAt.TryGetValue(peer.NodeId, out var seen) || seen != peer.LastSeen)
            {
                knownDevices.Remember(peer, configurations.GetValueOrDefault(peer.NodeId));
                rememberedAt[peer.NodeId] = peer.LastSeen;
            }
        }

        talkView.Grid.Update(peers, settings, VolumeFor, otaUpdating);
        talkView.OtherGroups.Update(BuildOtherGroups(peers));

        // Known-devices holds only records that are silent on every group; a
        // device announcing on a foreign group is in activeIds and excluded here.
        var activeIds = peers.Select(peer => peer.NodeId).ToHashSet();
        var offline = knownDevices.Devices.Where(device => !activeIds.Contains(device.NodeId)).ToArray();
        talkView.KnownDevices.Update(offline);

        if (settingsView.Group.Visible) RefreshGroupPage();
        if (settingsView.Firmware.Visible) RefreshOtaQueue();

        talkView.SetBroadcastScope(settings.BroadcastTargetLabel);
        UpdateLastSender();
        UpdateIdentityStrip();
        UpdateNowBar();
        UpdateDiagnostics();
    }

    private void UpdateLastSender()
    {
        var talker = receiveSession?.LastTalker;
        if (talker is null)
        {
            talkView.SetLastSender("Last sender: none");
            if (receiveSession is not null && !otaUpdating) talkView.Reply.Enabled = true;
            return;
        }
        var multi = settings.JoinedGroups.Count > 1;
        var joined = settings.IsJoined(talker.GroupCode);
        var suffix = !multi && joined ? "" : $" · {settings.GroupLabel(talker.GroupCode)}{(joined ? "" : " — not joined")}";
        talkView.SetLastSender($"Last sender: {talker.Alias}{suffix}");
        talkView.Reply.Enabled = receiveSession is not null && !otaUpdating && joined;
    }

    private int VolumeFor(uint nodeId) => configurations.GetValueOrDefault(nodeId)?.SpeakerVolume
        ?? knownDevices.Devices.FirstOrDefault(device => device.NodeId == nodeId)?.SpeakerVolume
        ?? 512;

    private void UpdateIdentityStrip()
    {
        var mic = TrimDeviceName((settingsView.Identity.MicCombo.SelectedItem as RecordingDevice)?.Name ?? "Not selected");
        var speaker = TrimDeviceName((settingsView.Identity.SpeakerCombo.SelectedItem as PlaybackDevice)?.Name ?? "Not selected");
        identityStrip.Update(settings, mic, speaker, StateColor());
    }

    /// <summary>Rows for "Other groups on this network": peers announcing on a
    /// group this companion has not joined, grouped by code.</summary>
    private IReadOnlyList<OtherGroupsView.Row> BuildOtherGroups(IReadOnlyList<Peer> peers)
    {
        return peers
            .Where(peer => !settings.IsJoined(peer.GroupCode))
            .GroupBy(peer => peer.GroupCode)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var aliases = group.OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase).Select(peer => peer.Alias).ToArray();
                var known = group.FirstOrDefault(peer => knownDevices.Devices.Any(device => device.NodeId == peer.NodeId));
                var (note, color) = known is not null
                    ? ($"you know {known.Alias} — it moved to this group", UiStyles.WarnText)
                    : ("no name set on this PC", UiStyles.Muted);
                return new OtherGroupsView.Row(group.Key, settings.GroupLabel(group.Key), aliases.Length, string.Join(", ", aliases), note, color);
            })
            .ToArray();
    }

    private Color StateColor() => degraded switch
    {
        "NETWORK UNAVAILABLE" => UiStyles.Red,
        "AUDIO UNAVAILABLE" => UiStyles.Amber,
        _ => (receiveSession?.State ?? IntercomState.Idle) switch
        {
            IntercomState.Receiving => UiStyles.Blue,
            IntercomState.Talking => UiStyles.Purple,
            IntercomState.Claiming or IntercomState.WaitingForFloor => UiStyles.Amber,
            _ => UiStyles.Green,
        },
    };

    // ---- Now bar -----------------------------------------------------------

    private void UpdateNowBar()
    {
        if (degraded is not null) return;
        var state = receiveSession?.State ?? IntercomState.Idle;
        var buffer = audio?.BufferedMilliseconds ?? 0;
        var talker = receiveSession?.LastTalker?.Alias ?? "A device";
        var receivingTitle = receiveSession?.ReceptionDirected == true
            ? $"{talker} is speaking to you" : $"{talker} is speaking";
        // When several groups are joined, the detail line leads with the sender's
        // group so it is clear which one is on the floor.
        var groupPrefix = settings.JoinedGroups.Count > 1 && receiveSession?.LastTalker is { } lt
            ? $"{settings.GroupLabel(lt.GroupCode)} · " : "";
        var (kicker, title, color, background) = state switch
        {
            IntercomState.Claiming => ("CLAIMING", "Claiming", UiStyles.Amber, UiStyles.AmberTint),
            IntercomState.Talking => ("TALKING", "Talking", UiStyles.Purple, UiStyles.PurpleTint),
            IntercomState.Receiving => ("RECEIVING", receivingTitle, UiStyles.Blue, UiStyles.BlueTint),
            IntercomState.WaitingForFloor => ("FLOOR OCCUPIED", "Floor occupied", UiStyles.Amber, UiStyles.AmberTint),
            _ => ("IDLE", "Idle", UiStyles.Green, UiStyles.Surface),
        };
        var detail = state switch
        {
            IntercomState.Receiving => $"{groupPrefix}{talker} · {(DateTimeOffset.UtcNow - receivingSince).TotalSeconds:0.0} s · output buffer {buffer} ms",
            IntercomState.Talking => $"talking to the group · output buffer {buffer} ms",
            IntercomState.Claiming => $"claiming the floor · output buffer {buffer} ms",
            IntercomState.WaitingForFloor => $"another device holds the floor · output buffer {buffer} ms",
            _ => $"nothing on the floor · output buffer {buffer} ms",
        };
        nowBar.Update(kicker, title, detail, color, background);
    }

    private void ShowDegraded(string kicker, string title, string detail, Color color, Color background)
    {
        degraded = kicker;
        nowBar.Update(kicker, title, detail, color, background);
        UpdateIdentityStrip();
        RecordActivity(detail);
    }

    private void OnIntercomState(IntercomState state)
    {
        if (state == IntercomState.Receiving) receivingSince = DateTimeOffset.UtcNow;
        if (degraded == "AUDIO UNAVAILABLE") degraded = null;
        UpdateNowBar();
        UpdateIdentityStrip();
        var talker = receiveSession?.LastTalker?.Alias ?? "A device";
        // Name the group on a received line when it is not the broadcast target,
        // so activity across several joined groups stays legible.
        var groupSuffix = receiveSession?.LastTalker is { } lt && lt.GroupCode != settings.BroadcastTargetCode
            ? $" · {settings.GroupLabel(lt.GroupCode)}" : "";
        RecordActivity(state switch
        {
            IntercomState.Claiming => "Claiming floor",
            IntercomState.Talking => "You — talking",
            IntercomState.Receiving when receiveSession?.ReceptionDirected == true => $"{talker} is speaking to you{groupSuffix}",
            IntercomState.Receiving => $"{talker} is speaking{groupSuffix}",
            IntercomState.WaitingForFloor => "Floor busy — your press was dropped",
            _ => "Idle",
        });
    }

    // ---- Talk handlers -----------------------------------------------------

    private void BindPtt(Button button, Action press)
    {
        button.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && !otaUpdating) { button.Capture = true; press(); } };
        button.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) receiveSession?.ReleasePtt(); };
        button.MouseCaptureChanged += (_, _) => { if (!button.Capture) receiveSession?.ReleasePtt(); };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Space || spaceHeld || otaUpdating) return;
        spaceHeld = true;
        receiveSession?.PressBroadcast();
        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Space) return;
        spaceHeld = false;
        receiveSession?.ReleasePtt();
        e.Handled = true;
    }

    private async Task ToggleSoftMuteAsync(Peer peer)
    {
        if (node is null || !peer.SupportsMuteReporting || peer.HardwareMuted || otaUpdating) return;
        try
        {
            var current = await node.RequestConfigurationAsync(peer);
            configurations[peer.NodeId] = current.Configuration;
            var reply = await node.SetConfigurationAsync(peer, current.Configuration with { SoftMute = !current.Configuration.SoftMute });
            configurations[peer.NodeId] = reply.Configuration;
            knownDevices.Remember(peer, reply.Configuration);
            RefreshPeers();
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException)
        {
            statusBar.SetMessage($"Silence failed: {exception.Message}");
        }
    }

    private async Task CommitVolumeAsync(Peer peer, int value)
    {
        if (node is null || otaUpdating) return;
        try
        {
            var current = configurations.GetValueOrDefault(peer.NodeId) ?? (await node.RequestConfigurationAsync(peer)).Configuration;
            var reply = await node.SetConfigurationAsync(peer, current with { SpeakerVolume = value });
            configurations[peer.NodeId] = reply.Configuration;
            knownDevices.Remember(peer, reply.Configuration);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException)
        {
            statusBar.SetMessage($"Volume update failed: {exception.Message}");
        }
    }

    private void ShowDeviceMenu(Control card, Peer peer)
    {
        var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.Professional };
        menu.Renderer = new ToolStripProfessionalRenderer(new FlatMenuColors()) { RoundedEdges = false };
        menu.ShowImageMargin = false;
        menu.Font = UiStyles.BodyFont;
        menu.Items.Add("Configure…", null, async (_, _) => await ConfigurePeerAsync(peer));
        menu.Items.Add("Get configuration", null, async (_, _) => await GetPeerConfigurationAsync(peer));
        menu.Items.Add("Move to another group…", null, async (_, _) => await ConfigurePeerAsync(peer, focusGroup: true));
        menu.Items.Add("Update firmware…", null, (_, _) => { queuedOtaDevices.Add(peer.NodeId); OpenSettings("Firmware"); RefreshOtaQueue(); });
        menu.Items.Add("Remove", null, (_, _) => RemoveKnownDevice(ToKnownDevice(peer)));
        menu.Show(card, new Point(card.Width - 28, 65));
    }

    private async Task GetPeerConfigurationAsync(Peer peer)
    {
        if (node is null) return;
        try
        {
            var reply = await node.RequestConfigurationAsync(peer);
            configurations[peer.NodeId] = reply.Configuration;
            knownDevices.Remember(peer, reply.Configuration);
            RefreshPeers();
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException)
        {
            statusBar.SetMessage($"Configuration read failed: {exception.Message}");
        }
    }

    private async Task ConfigurePeerAsync(Peer peer, bool focusGroup = false)
    {
        if (node is null) return;
        try
        {
            var current = (await node.RequestConfigurationAsync(peer)).Configuration;
            configurations[peer.NodeId] = current;
            using var dialog = new ConfigureDeviceDialog(peer, current,
                requestedId => node.Peers.Any(other => other.NodeId != peer.NodeId && other.NodeId == requestedId),
                GroupChoices(current.MeshId), focusGroup, () => node.Peers);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is not { } next) return;
            var reply = await node.SetConfigurationAsync(peer, next);
            configurations[peer.NodeId] = reply.Configuration;
            knownDevices.Remember(peer, reply.Configuration);
            RefreshPeers();
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException)
        {
            statusBar.SetMessage($"Configuration update failed: {exception.Message}");
        }
    }

    private IReadOnlyList<ConfigureDeviceDialog.GroupChoice> GroupChoices(string currentCode)
    {
        var choices = settings.JoinedGroups.Select(group => new ConfigureDeviceDialog.GroupChoice(group.Code, group.Label)).ToList();
        if (!settings.IsJoined(currentCode) && Protocol.IsValidMeshId(currentCode))
            choices.Insert(0, new ConfigureDeviceDialog.GroupChoice(currentCode, currentCode));
        return choices;
    }

    private void RemoveKnownDevice(KnownDevice device)
    {
        if (!RemoveDeviceDialog.ConfirmRemove(this, device.Alias, settings.MeshId)) return;
        knownDevices.Remove(device.NodeId);
        RefreshPeers();
    }

    private void RemoveAllOffline(int count)
    {
        if (!RemoveDeviceDialog.ConfirmRemoveAll(this, count)) return;
        var activeIds = displayPeers.Select(peer => peer.NodeId).ToHashSet();
        knownDevices.RemoveAllOffline(activeIds);
        RefreshPeers();
    }

    private static KnownDevice ToKnownDevice(Peer peer) =>
        new(peer.NodeId, peer.Alias, peer.Endpoint.Address.ToString(), peer.LastSeen, null, null, peer.SoftMuted);

    // ---- Settings page changes --------------------------------------------

    private void OnSettingsPageShown(string page)
    {
        switch (page)
        {
            case "Firmware": RefreshOtaQueue(); break;
            case "Diagnostics": lastLogLength = -1; UpdateDiagnostics(); break;
            case "Group": RefreshGroupPage(); break;
        }
    }

    private void SaveIdentitySettings()
    {
        if (node is null) return;
        node.SetAlias(settingsView.Identity.AliasText);
        settings.Alias = node.Alias;
        settings.RunInNotificationArea = settingsView.Identity.RunInNotificationArea;
        settings.StartWithWindows = settingsView.Identity.StartWithWindows;
        settings.Save();
        settingsView.Identity.SetAlias(settings.Alias);
        ApplyAudioDeviceSelection();
        UpdateIdentityStrip();
        statusBar.SetMessage("Identity, audio and shortcut settings saved.");
    }

    // ---- Audio -------------------------------------------------------------

    private void PopulateAudioDevices()
    {
        var mics = AudioEngine.RecordingDevices();
        settingsView.Identity.MicCombo.Items.Clear();
        settingsView.Identity.MicCombo.Items.AddRange(mics.Cast<object>().ToArray());
        settingsView.Identity.MicCombo.SelectedItem = mics.FirstOrDefault(device => device.Name == settings.RecordingDeviceName) ?? mics.FirstOrDefault();

        var speakers = AudioEngine.PlaybackDevices();
        settingsView.Identity.SpeakerCombo.Items.Clear();
        settingsView.Identity.SpeakerCombo.Items.AddRange(speakers.Cast<object>().ToArray());
        settingsView.Identity.SpeakerCombo.SelectedItem = speakers.FirstOrDefault(device => device.Id == settings.PlaybackDeviceId) ?? speakers.FirstOrDefault();
    }

    private void StartAudioEngine()
    {
        audio = new AudioEngine(settings.RecordingDeviceName, settings.PlaybackDeviceId);
        audio.Diagnostic += message => PostToUi(() => statusBar.SetMessage(message));
        audio.StartPlayback();
        receiveSession = new ReceiveSession(node!, audio);
        receiveSession.StateChanged += state => PostToUi(() => OnIntercomState(state));
        receiveSession.Diagnostic += message => PostToUi(() => statusBar.SetMessage(message));
        receiveSession.Start();
    }

    private void ApplyAudioDeviceSelection()
    {
        var recorder = settingsView.Identity.MicCombo.SelectedItem as RecordingDevice;
        var playback = settingsView.Identity.SpeakerCombo.SelectedItem as PlaybackDevice;
        if (recorder is null || playback is null) return;
        if (receiveSession?.State != IntercomState.Idle)
        {
            statusBar.SetMessage("Release the floor before changing audio devices.");
            return;
        }
        receiveSession?.Stop();
        audio?.Dispose();
        settings.RecordingDeviceName = recorder.Name;
        settings.PlaybackDeviceId = playback.Id;
        settings.Save();
        try
        {
            StartAudioEngine();
            degraded = null;
            statusBar.SetMessage($"Audio devices applied: {recorder.Name} → {playback.Name}");
            UpdateIdentityStrip();
            UpdateNowBar();
        }
        catch (Exception exception)
        {
            ShowDegraded("AUDIO UNAVAILABLE", "Audio unavailable", $"Audio device change failed: {exception.Message}", UiStyles.Amber, UiStyles.AmberTint);
        }
    }

    // ---- USB ---------------------------------------------------------------

    private async Task RescanUsbAsync()
    {
        var ports = UsbConfigurationClient.GetPortNames();
        settingsView.Usb.SetPorts(ports.Select(port => new UsbPage.PortOption(port, port)).ToArray(), settingsView.Usb.SelectedPort);
        if (ports.Count == 0)
        {
            settingsView.Usb.SetIdentity("No serial port found. Connect the device by USB.", UiStyles.Secondary);
            return;
        }
        await IdentifySelectedPortAsync();
    }

    private async Task IdentifySelectedPortAsync()
    {
        var port = settingsView.Usb.SelectedPort;
        if (string.IsNullOrWhiteSpace(port)) return;
        settingsView.Usb.SetIdentity($"Identifying {port}…", UiStyles.Secondary);
        try
        {
            var configuration = await UsbConfigurationClient.GetConfigurationAsync(port);
            settingsView.Usb.SetPorts([new UsbPage.PortOption(port, $"{port} — {configuration.Alias} ({configuration.DeviceId:x8})")], port);
            settingsView.Usb.SetAlias(configuration.Alias);
            if (!string.Equals(configuration.Ssid, "YOUR_WIFI_SSID", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(configuration.Ssid))
                settingsView.Usb.SetSsids([configuration.Ssid], configuration.Ssid);
            settingsView.Usb.SetIdentity("Device identified over USB", UiStyles.Green);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            settingsView.Usb.SetIdentity($"Could not identify the device: {exception.Message}", UiStyles.Red);
        }
    }

    private async Task ApplyUsbWifiAsync()
    {
        var port = settingsView.Usb.SelectedPort;
        var ssid = settingsView.Usb.Ssid;
        if (string.IsNullOrWhiteSpace(port) || string.IsNullOrWhiteSpace(ssid))
        {
            settingsView.Usb.SetIdentity("Choose a USB port and a Wi-Fi network first.", UiStyles.Red);
            return;
        }
        settingsView.Usb.SetIdentity($"Writing Wi-Fi configuration to {port}…", UiStyles.Secondary);
        try
        {
            var configuration = await UsbConfigurationClient.SetWifiAsync(port, ssid, settingsView.Usb.Password, settingsView.Usb.DeviceAlias);
            settingsView.Usb.ClearPassword();
            settingsView.Usb.SetIdentity($"Wi-Fi saved for {configuration.Alias}. The device is rebooting onto the network…", UiStyles.Green);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            settingsView.Usb.SetIdentity($"USB update failed: {exception.Message}", UiStyles.Red);
        }
    }

    private async Task RefreshSsidsAsync()
    {
        try
        {
            var ssids = await Task.Run(ScanSsids);
            if (ssids.Count > 0) PostToUi(() => settingsView.Usb.SetSsids(ssids, null));
        }
        catch { /* SSID scanning is a best-effort convenience. */ }
    }

    private static IReadOnlyList<string> ScanSsids()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", "wlan show networks")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            return output.Split('\n')
                .Where(line => line.TrimStart().StartsWith("SSID ", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                .Select(line => line[(line.IndexOf(':') + 1)..].Trim())
                .Where(ssid => ssid.Length > 0)
                .Distinct()
                .ToArray();
        }
        catch { return []; }
    }

    // ---- Group -------------------------------------------------------------

    private void RefreshGroupPage()
    {
        settingsView.Group.SetCompanionId(settings.NodeId);
        var all = settings.BroadcastsToAll;
        var rows = settings.JoinedGroups.Select(group =>
        {
            var isTarget = !all && group.Code == settings.BroadcastTargetCode;
            var note = all ? "receiving + broadcast" : isTarget ? "broadcast target" : "receiving only";
            return new GroupIdsPage.GroupRow(group.Code, group.Label,
                displayPeers.Count(peer => peer.GroupCode == group.Code), isTarget, note);
        }).ToArray();
        settingsView.Group.SetGroups(rows, canLeave: settings.JoinedGroups.Count > 1, broadcastAll: all);
        settingsView.Group.SetDestinations(settings.JoinedGroups.Select(group => group.Code).ToArray());
        settingsView.Group.SetMovable(BuildMovable());

        var foreign = displayPeers.Where(peer => !settings.IsJoined(peer.GroupCode))
            .Select(peer => peer.GroupCode).Distinct().OrderBy(code => code, StringComparer.Ordinal).ToArray();
        settingsView.Group.SetAnnouncingHint(foreign.Length switch
        {
            0 => "",
            1 => $"{foreign[0]} is announcing nearby",
            _ => $"{string.Join(", ", foreign[..^1])} and {foreign[^1]} are announcing nearby",
        });
    }

    private IReadOnlyList<GroupIdsPage.Movable> BuildMovable()
    {
        var list = new List<GroupIdsPage.Movable>();
        var seen = new HashSet<uint>();
        foreach (var peer in displayPeers.Where(peer => settings.IsJoined(peer.GroupCode)).OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase))
        {
            seen.Add(peer.NodeId);
            var legacy = peer.ProtocolVersion is null or 1;
            var (reach, color, selectable) = legacy
                ? ("legacy, USB only", UiStyles.WarnText, false)
                : ("reachable", UiStyles.Green, true);
            list.Add(new GroupIdsPage.Movable(peer.NodeId, peer.Alias, $"{peer.NodeId:x8}", settings.GroupLabel(peer.GroupCode), reach, color, selectable));
        }
        foreach (var device in knownDevices.Devices.Where(device => !seen.Contains(device.NodeId)).OrderBy(device => device.Alias, StringComparer.OrdinalIgnoreCase))
            list.Add(new GroupIdsPage.Movable(device.NodeId, device.Alias, $"{device.NodeId:x8}", "not responding", "unreachable", UiStyles.Muted, false));
        return list;
    }

    private void RenameGroup(string code)
    {
        var name = PromptForText($"Name for group {code} on this PC", settings.GroupName(code) ?? "");
        if (name is null) return;
        node?.RenameGroup(code, name);
        talkView.SetBroadcastScope(settings.BroadcastTargetLabel);
        RefreshPeers();
    }

    private async Task MoveDevicesAsync()
    {
        if (node is null) return;
        var destination = settingsView.Group.DestinationCode;
        if (!Protocol.IsValidMeshId(destination)) return;
        var ids = settingsView.Group.CheckedDevices.ToHashSet();
        var targets = displayPeers.Where(peer => ids.Contains(peer.NodeId)).ToArray();
        if (targets.Length == 0) return;

        if (!settings.IsJoined(destination))
        {
            var choice = MessageBox.Show(this,
                $"You are not in {destination}. Join it as well, so the moved devices stay visible?",
                "Move devices", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) return;
            if (choice == DialogResult.Yes) node.JoinGroup(destination);
        }

        var moved = 0;
        var failures = new List<string>();
        foreach (var peer in targets)
        {
            try
            {
                var current = (await node.RequestConfigurationAsync(peer)).Configuration;
                await node.SetConfigurationAsync(peer, current with { MeshId = destination });
                moved++;
                RecordActivity($"{peer.Alias} moving to {settings.GroupLabel(destination)}");
            }
            catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException)
            {
                failures.Add($"{peer.Alias} did not acknowledge");
            }
        }
        settingsView.Group.ResetMove();
        RefreshPeers();
        var report = failures.Count == 0
            ? $"{moved} moved to {destination}."
            : $"{moved} moved, {failures.Count} failed — {failures[0]}";
        RecordActivity(report);
        MessageBox.Show(this, report, "Move devices", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private string? PromptForText(string prompt, string initial)
    {
        using var dialog = new Form
        {
            Text = prompt, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, BackColor = UiStyles.White,
            Font = new Font("Segoe UI", 9f), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var box = new TextBox { Text = initial, MaxLength = 24 };
        UiStyles.StyleInput(box, 320);
        var ok = UiKit.PrimaryButton("OK");
        ok.DialogResult = DialogResult.OK;
        ok.Margin = new Padding(0, 0, 8, 0);
        var cancel = UiKit.PlainButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
        actions.Controls.Add(ok);
        actions.Controls.Add(cancel);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20, 20, 20, 16), BackColor = UiStyles.White };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(box, 0, 0);
        root.Controls.Add(actions, 0, 1);
        dialog.Controls.Add(root);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }

    private void ChangeBroadcastTarget(string code)
    {
        if (node is null || code == settings.BroadcastTargetCode) return;
        node.SetBroadcastTarget(code);
        talkView.SetBroadcastScope(settings.BroadcastTargetLabel);
        RecordActivity($"Broadcasting to {settings.BroadcastTargetLabel}");
        RefreshPeers();
    }

    // ---- OTA ---------------------------------------------------------------

    private void ChooseOtaPackage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose signed Wi-Fi Intercom OTA package",
            Filter = "OTA packages (*.ota.json;*.json)|*.ota.json;*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            otaPackage = OtaPackage.Load(dialog.FileName);
            settingsView.Firmware.SetPackage($"wifi_intercom {otaPackage.Version}",
                $"{Path.GetFileName(otaPackage.ManifestPath)} · {otaPackage.Size / 1024.0:0.0} KiB · signature verified");
            settingsView.Firmware.SetStatus("Package verified. Add compatible devices and start the queue.", UiStyles.Green);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or CryptographicException)
        {
            otaPackage = null;
            settingsView.Firmware.SetPackage(null, null);
            settingsView.Firmware.SetStatus($"OTA package rejected: {exception.Message}", UiStyles.Red);
        }
        RefreshOtaQueue();
    }

    private void RefreshOtaQueue()
    {
        var version = otaPackage?.Version ?? "selected package";
        var rows = new List<FirmwarePage.QueueRow>();
        foreach (var nodeId in queuedOtaDevices.OrderBy(id => displayPeers.FirstOrDefault(peer => peer.NodeId == id)?.Alias ?? "￿"))
        {
            var peer = displayPeers.FirstOrDefault(peer => peer.NodeId == nodeId);
            if (peer is not null && peer.IsOtaEligible)
            {
                if (!otaProgress.TryGetValue(nodeId, out var progress))
                    progress = ("Queued", UiStyles.Muted, 0, "Waiting for earlier devices.");
                rows.Add(new FirmwarePage.QueueRow(nodeId, peer.Alias, $"{peer.FirmwareVersion}  →  {version}",
                    progress.State, progress.Color, progress.Progress, "Remove", progress.Detail, true));
            }
            else
            {
                var alias = knownDevices.Devices.FirstOrDefault(device => device.NodeId == nodeId)?.Alias ?? $"{nodeId:x8}";
                rows.Add(new FirmwarePage.QueueRow(nodeId, alias, $"unknown  →  {version}", "Not eligible", UiStyles.Red, 0, "Remove",
                    "Not currently announcing, or without the OTA capability. It rejoins when it announces.", true));
            }
        }
        settingsView.Firmware.SetQueue(rows);
    }

    private void OnOtaStatus(OtaStatusReceivedEventArgs args)
    {
        var status = args.Status;
        var failed = status.State is "failed" or "rejected";
        var color = failed ? UiStyles.Red : status.State == "rebooting" ? UiStyles.Green : UiStyles.Amber;
        otaProgress[args.NodeId] = ($"{status.State} {status.Progress}%", color, status.Progress, status.Message);
        if (settingsView.Firmware.Visible) RefreshOtaQueue();
    }

    private async Task RunOtaQueueAsync()
    {
        if (node is null || otaPackage is null)
        {
            settingsView.Firmware.SetStatus("Choose a signed package before starting the queue.", UiStyles.Red);
            return;
        }
        if (receiveSession?.State != IntercomState.Idle)
        {
            settingsView.Firmware.SetStatus("Release the local PTT floor before starting an update.", UiStyles.Red);
            return;
        }
        var targets = displayPeers.Where(peer => queuedOtaDevices.Contains(peer.NodeId) && peer.IsOtaEligible)
            .OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0)
        {
            settingsView.Firmware.SetStatus("Add at least one active, protocol p2-compatible device to the queue.", UiStyles.Red);
            return;
        }

        var package = otaPackage;
        var summary = $"{Path.GetFileName(package.ManifestPath)} · {package.Size / 1024.0:0.0} KiB · signature verified";
        var confirmTargets = targets.Select(peer => new ConfirmFirmwareUpdateDialog.Target(peer.Alias, peer.NodeId, peer.FirmwareVersion, package.Version)).ToArray();
        if (!ConfirmFirmwareUpdateDialog.Confirm(this, package.Version, summary, confirmTargets)) return;

        otaUpdating = true;
        talkView.Broadcast.Enabled = talkView.Reply.Enabled = false;
        RefreshPeers();
        try
        {
            await using var server = new OtaArtifactServer(node.DiscoveryInterface, package);
            foreach (var peer in targets)
            {
                otaProgress[peer.NodeId] = ("Offering", UiStyles.Amber, 5, $"Offering {package.Version} to {peer.Alias} ({peer.Endpoint.Address})…");
                RefreshOtaQueue();
                settingsView.Firmware.SetStatus($"Offering {package.Version} to {peer.Alias}…", UiStyles.Secondary);
                var update = node.RequestOtaUpdateAsync(peer, package, server, otaStopping.Token);
                var request = server.WaitForFirstRequestAsync(otaStopping.Token).WaitAsync(TimeSpan.FromSeconds(15), otaStopping.Token);
                try
                {
                    if (await Task.WhenAny(update, request) == update) await update;
                    await request;
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"{peer.Alias} accepted the update but could not reach the companion's firmware server. Check the private-network firewall prompt and retry.");
                }
                await update;
                settingsView.Firmware.SetStatus($"{peer.Alias} is rebooting; waiting for its {package.Version} announcement…", UiStyles.Secondary);
                if (!await WaitForUpdatedPeerAsync(peer.NodeId, package.Version, otaStopping.Token))
                    throw new TimeoutException($"{peer.Alias} did not report {package.Version} after reboot.");
                otaProgress[peer.NodeId] = ("Confirmed", UiStyles.Green, 100, $"Confirmed {package.Version}.");
                RefreshOtaQueue();
            }
            settingsView.Firmware.SetStatus($"OTA completed: {targets.Length} confirmed on {package.Version}.", UiStyles.Green);
        }
        catch (OperationCanceledException) { settingsView.Firmware.SetStatus("OTA stopped while the companion was closing.", UiStyles.Secondary); }
        catch (Exception exception) when (exception is SocketException or TimeoutException or IOException or InvalidOperationException or InvalidDataException)
        {
            settingsView.Firmware.SetStatus($"OTA stopped: {exception.Message}", UiStyles.Red);
        }
        finally
        {
            otaUpdating = false;
            talkView.Broadcast.Enabled = talkView.Reply.Enabled = true;
            RefreshPeers();
        }
    }

    private async Task<bool> WaitForUpdatedPeerAsync(uint nodeId, string version, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (node?.Peers.FirstOrDefault(peer => peer.NodeId == nodeId) is { } peer &&
                peer.FirmwareVersion == version && peer.ProtocolVersion == Protocol.Version)
                return true;
            await Task.Delay(500, cancellationToken);
        }
        return false;
    }

    // ---- Diagnostics -------------------------------------------------------

    private void UpdateDiagnostics()
    {
        var stats = receiveSession?.Statistics;
        settingsView.Diagnostics.SetCounters(
            $"{stats?.AudioPackets ?? 0:n0}",
            $"{stats?.DecodedFrames ?? 0:n0}",
            $"{stats?.ConcealedFrames ?? 0:n0}",
            $"{audio?.BufferedMilliseconds ?? 0} ms");
        statusBar.SetMessage($"Discovery: listening on {DiscoveryEndpoint} · audio 16 kHz mono ADPCM · UDP {stats?.AudioPackets ?? 0:n0} · PLC {stats?.ConcealedFrames ?? 0:n0} · buffer {audio?.BufferedMilliseconds ?? 0} ms");

        // The log is the expensive part: reading and recolouring the whole file
        // every second blocked the UI. Re-render only when the page is visible and
        // the file has actually grown, and do the read off the UI thread.
        if (!settingsView.Diagnostics.Visible) return;
        long length;
        try { length = new FileInfo(DiagnosticLogPath).Length; } catch { return; }
        if (length == lastLogLength) return;
        lastLogLength = length;
        _ = RefreshDiagnosticLogAsync();
    }

    private async Task RefreshDiagnosticLogAsync()
    {
        IReadOnlyList<(string, Color)> lines;
        try { lines = await Task.Run(() => FormatDiagnosticLog(ReadDiagnosticLogTail())); }
        catch { return; }
        if (!IsDisposed && settingsView.Diagnostics.Visible) settingsView.Diagnostics.SetLogLines(lines);
    }

    private static IReadOnlyList<(string, Color)> FormatDiagnosticLog(string source)
    {
        var lines = new List<(string, Color)>();
        var rows = source.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        // Only the most recent lines are worth rendering; the visible area holds a
        // handful and unbounded recolouring is what made the page slow.
        const int maxRows = 200;
        foreach (var raw in rows.Length > maxRows ? rows[^maxRows..] : rows)
        {
            var timestamp = raw.Length >= 19 && DateTimeOffset.TryParse(raw[..Math.Min(raw.Length, 34)], out var parsed)
                ? parsed.ToLocalTime().ToString("HH:mm:ss") : DateTime.Now.ToString("HH:mm:ss");
            var detail = raw.Length >= 34 && raw[10] == 'T' ? raw[34..].TrimStart() : raw;
            var color = detail.Contains("error", StringComparison.OrdinalIgnoreCase) || detail.Contains("failed", StringComparison.OrdinalIgnoreCase) || detail.Contains("expire", StringComparison.OrdinalIgnoreCase) ? UiStyles.LogError
                : detail.Contains("audio", StringComparison.OrdinalIgnoreCase) || detail.Contains("claim", StringComparison.OrdinalIgnoreCase) ? UiStyles.LogInfo
                : detail.Contains("busy", StringComparison.OrdinalIgnoreCase) || detail.Contains("mute", StringComparison.OrdinalIgnoreCase) ? UiStyles.LogWarning
                : UiStyles.Disabled;
            lines.Add(($"{timestamp}  {detail.ToUpperInvariant()}", color));
        }
        return lines;
    }

    private static string DiagnosticLogPath => Path.Combine(CompanionSettings.AppDataDirectory, "diagnostics.log");

    private static string ReadDiagnosticLog()
    {
        try
        {
            using var stream = new FileStream(DiagnosticLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return "No diagnostic log is available."; }
    }

    /// <summary>Reads only the last 64 KB of the log, sharing access with the
    /// writer and dropping the leading partial line, so the diagnostics page can
    /// render without loading a multi-megabyte file.</summary>
    private static string ReadDiagnosticLogTail()
    {
        try
        {
            using var stream = new FileStream(DiagnosticLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int tail = 64 * 1024;
            var seeked = stream.Length > tail;
            if (seeked) stream.Seek(-tail, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (seeked)
            {
                var newline = text.IndexOf('\n');
                if (newline >= 0) text = text[(newline + 1)..];
            }
            return text;
        }
        catch (IOException) { return "No diagnostic log is available."; }
    }

    private void SaveDiagnosticLog()
    {
        using var dialog = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt", FileName = "wifi-intercom-diagnostics.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, ReadDiagnosticLog()); }
        catch (IOException exception) { MessageBox.Show(this, exception.Message, "Could not save log", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ---- Activity / recordings --------------------------------------------

    private void RecordActivity(string text) => talkView.Activity.Add(text);

    private static void OpenRecordingsFolder()
    {
        try { Process.Start(new ProcessStartInfo(CompanionSettings.RecordingsDirectory) { UseShellExecute = true }); } catch { }
    }

    // ---- Tray --------------------------------------------------------------

    private void EnableTray()
    {
        if (trayIcon is not null) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Exit", null, (_, _) => { allowExit = true; Close(); });
        trayIcon = new NotifyIcon
        {
            Text = "Intercom Companion",
            Icon = BrandAssets.AppIconAt(SystemInformation.SmallIconSize.Width),
            ContextMenuStrip = menu,
            Visible = true,
        };
        trayIcon.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
    }

    private bool KeepRunningInTray(FormClosingEventArgs e)
    {
        if (!allowExit && e.CloseReason == CloseReason.UserClosing && settings.RunInNotificationArea)
        {
            e.Cancel = true;
            Hide();
            trayIcon?.ShowBalloonTip(1000, "Wi-Fi Intercom", "Still running in the notification area; hotkeys remain active.", ToolTipIcon.Info);
            return true;
        }
        if (trayIcon is not null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
        return false;
    }

    // ---- Helpers -----------------------------------------------------------

    private static string DiscoveryEndpoint => $"{IntercomNode.MulticastGroup}:{Protocol.Port}";

    private static string TrimDeviceName(string value)
    {
        value = value.Replace("Microphone (", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Headset Earphone (", "", StringComparison.OrdinalIgnoreCase).TrimEnd(')');
        return value.Length <= 23 ? value : $"{value[..20]}…";
    }
}

/// <summary>Flat colour table for the card overflow menu — white, 1 px grey
/// border, no gradient, hover <c>#F2F4F5</c>.</summary>
internal sealed class FlatMenuColors : ProfessionalColorTable
{
    public override Color MenuBorder => UiStyles.Border;
    public override Color MenuItemBorder => UiStyles.Border;
    public override Color MenuItemSelected => UiStyles.HairRule;
    public override Color MenuItemSelectedGradientBegin => UiStyles.HairRule;
    public override Color MenuItemSelectedGradientEnd => UiStyles.HairRule;
    public override Color ToolStripDropDownBackground => UiStyles.White;
    public override Color ImageMarginGradientBegin => UiStyles.White;
    public override Color ImageMarginGradientMiddle => UiStyles.White;
    public override Color ImageMarginGradientEnd => UiStyles.White;
}
