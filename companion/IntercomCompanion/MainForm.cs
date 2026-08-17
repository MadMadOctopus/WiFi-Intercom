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

        talkView.SetBroadcastScope(settings.MeshId);
        settingsView.Group.SetCompanionId(settings.NodeId);
        settingsView.Identity.SetCompanionId(settings.NodeId);
        settingsView.Identity.SetAlias(settings.Alias);
        settingsView.Identity.SetWindowOptions(settings.RunInNotificationArea, settings.StartWithWindows);
        settingsView.Group.SetContext(settings.MeshId, 0);
        UpdateIdentityStrip();
        UpdateNowBar();
    }

    // ---- Event wiring ------------------------------------------------------

    private void WireEvents()
    {
        identityStrip.ChangeAudioClicked += (_, _) => OpenSettings("Identity");
        identityStrip.SettingsClicked += (_, _) => OpenSettings("USB");
        identityStrip.LocalMuteToggled += (_, muted) => audio?.SetLocalPlaybackMuted(muted);

        BindPtt(talkView.Broadcast, () => receiveSession?.PressBroadcast());
        BindPtt(talkView.Reply, () => receiveSession?.PressReply());

        talkView.Grid.TalkPressed += peer => { if (!otaUpdating) receiveSession?.PressSelected(peer); };
        talkView.Grid.TalkReleased += () => receiveSession?.ReleasePtt();
        talkView.Grid.SilenceClicked += async peer => await ToggleSoftMuteAsync(peer);
        talkView.Grid.MoreClicked += ShowDeviceMenu;
        talkView.Grid.VolumeCommitted += async (peer, value) => await CommitVolumeAsync(peer, value);

        talkView.KnownDevices.RemoveClicked += RemoveKnownDevice;
        talkView.KnownDevices.RemoveAllClicked += RemoveAllOffline;
        talkView.Activity.OpenRecordingsClicked += (_, _) => OpenRecordingsFolder();
        talkView.Activity.DiagnosticsClicked += (_, _) => OpenSettings("Diagnostics");

        settingsView.PageShown += OnSettingsPageShown;
        settingsView.BackToTalkClicked += () => shell.ShowTalk();
        settingsView.Usb.RescanClicked += async (_, _) => await RescanUsbAsync();
        settingsView.Usb.SendClicked += async (_, _) => await ApplyUsbWifiAsync();
        settingsView.Group.ApplyClicked += async (_, _) => await ApplyGroupChangeAsync();
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

        talkView.Grid.Update(peers, settings.MeshId, VolumeFor, otaUpdating);

        var activeIds = peers.Select(peer => peer.NodeId).ToHashSet();
        var offline = knownDevices.Devices.Where(device => !activeIds.Contains(device.NodeId)).ToArray();
        talkView.KnownDevices.Update(offline);

        settingsView.Group.SetContext(settings.MeshId, peers.Length);
        if (settingsView.Firmware.Visible) RefreshOtaQueue();

        talkView.SetLastSender(receiveSession?.LastTalker?.Alias);
        UpdateIdentityStrip();
        UpdateNowBar();
        UpdateDiagnostics();
    }

    private int VolumeFor(uint nodeId) => configurations.GetValueOrDefault(nodeId)?.SpeakerVolume
        ?? knownDevices.Devices.FirstOrDefault(device => device.NodeId == nodeId)?.SpeakerVolume
        ?? 512;

    private void UpdateIdentityStrip()
    {
        var mic = TrimDeviceName((settingsView.Identity.MicCombo.SelectedItem as RecordingDevice)?.Name ?? "Not selected");
        var speaker = TrimDeviceName((settingsView.Identity.SpeakerCombo.SelectedItem as PlaybackDevice)?.Name ?? "Not selected");
        identityStrip.Update(settings.Alias, settings.NodeId, settings.MeshId, displayPeers.Count, mic, speaker, StateColor());
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
        // A directed reception with no audio reaching us is two other devices
        // talking privately: name it as such and use a muted accent, since this
        // companion is not playing it.
        var directedElsewhere = receiveSession is { ReceptionDirected: true, ReceptionHasAudio: false };
        var directedToUs = receiveSession is { ReceptionDirected: true, ReceptionHasAudio: true };
        var receivingTitle = directedElsewhere ? $"{talker} is speaking to another device"
            : directedToUs ? $"{talker} is replying to you"
            : $"{talker} is speaking";
        var (kicker, title, color, background) = state switch
        {
            IntercomState.Claiming => ("CLAIMING", "Claiming", UiStyles.Amber, UiStyles.AmberTint),
            IntercomState.Talking => ("TALKING", "Talking", UiStyles.Purple, UiStyles.PurpleTint),
            IntercomState.Receiving when directedElsewhere => ("RECEIVING", receivingTitle, UiStyles.Secondary, UiStyles.Surface),
            IntercomState.Receiving => ("RECEIVING", receivingTitle, UiStyles.Blue, UiStyles.BlueTint),
            IntercomState.WaitingForFloor => ("FLOOR OCCUPIED", "Floor occupied", UiStyles.Amber, UiStyles.AmberTint),
            _ => ("IDLE", "Idle", UiStyles.Green, UiStyles.Surface),
        };
        var detail = state switch
        {
            IntercomState.Receiving when directedElsewhere => $"directed to another device · not played here · {(DateTimeOffset.UtcNow - receivingSince).TotalSeconds:0.0} s",
            IntercomState.Receiving => $"{talker} · {(DateTimeOffset.UtcNow - receivingSince).TotalSeconds:0.0} s · output buffer {buffer} ms",
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
        RecordActivity(state switch
        {
            IntercomState.Claiming => "Claiming floor",
            IntercomState.Talking => "You — talking",
            IntercomState.Receiving when receiveSession?.ReceptionDirected == true => $"{talker} is speaking to another device",
            IntercomState.Receiving => $"{talker} is speaking",
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
        catch (Exception exception) when (exception is SocketException or TimeoutException)
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
        catch (Exception exception) when (exception is SocketException or TimeoutException)
        {
            statusBar.SetMessage($"Configuration read failed: {exception.Message}");
        }
    }

    private async Task ConfigurePeerAsync(Peer peer)
    {
        if (node is null) return;
        try
        {
            var current = (await node.RequestConfigurationAsync(peer)).Configuration;
            configurations[peer.NodeId] = current;
            using var dialog = new ConfigureDeviceDialog(peer, current,
                requestedId => node.Peers.Any(other => other.NodeId != peer.NodeId && other.NodeId == requestedId));
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
            case "Group": settingsView.Group.SetContext(settings.MeshId, displayPeers.Count); break;
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

    private async Task ApplyGroupChangeAsync()
    {
        if (node is null) return;
        var targetGroup = settingsView.Group.NewGroupId;
        if (!Protocol.IsValidMeshId(targetGroup)) return;
        var targets = settingsView.Group.ApplyToActiveDevices
            ? node.SnapshotActivePeers().Where(peer => peer.ProtocolVersion is >= 2).ToArray()
            : [];
        foreach (var peer in targets)
        {
            try
            {
                var current = (await node.RequestConfigurationAsync(peer)).Configuration;
                await node.SetConfigurationAsync(peer, current with { MeshId = targetGroup });
                RecordActivity($"Group update queued for {peer.Alias}.");
            }
            catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException)
            {
                RecordActivity($"Group update failed for {peer.Alias}: {exception.Message}");
            }
        }
        if (settingsView.Group.ApplyToCompanion)
        {
            node.SetMeshId(targetGroup);
            settings.MeshId = targetGroup;
            talkView.SetBroadcastScope(settings.MeshId);
        }
        settingsView.Group.Reset();
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
