using IntercomCompanion.Core;
using IntercomCompanion.Audio;
using System.Security.Cryptography;
using System.Net.Sockets;

namespace IntercomCompanion;

internal sealed partial class MainForm
{
    private readonly KnownDevicesStore knownDevices = KnownDevicesStore.Load();
    private readonly Dictionary<uint, DeviceCard> deviceCards = [];
    private readonly Dictionary<uint, DeviceConfiguration> configurations = [];
    private readonly Dictionary<uint, DateTimeOffset> rememberedAt = [];
    private readonly FlowLayoutPanel deviceGrid = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, BackColor = Color.FromArgb(245, 246, 247), Padding = new Padding(10, 8, 2, 8) };
    private readonly FlowLayoutPanel offlineDevices = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.White, Padding = new Padding(10) };
    private readonly TextBox deviceFilter = new() { Width = 145, PlaceholderText = "Alias or ID" };
    private readonly Label deviceCount = new() { AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly Label nowDetail = new() { AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109), Font = new Font("Segoe UI", 9) };
    private readonly ListBox activity = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 8) };
    private readonly Panel pageHost = new() { Dock = DockStyle.Fill };
    private readonly Panel talkPage = new() { Dock = DockStyle.Fill };
    private readonly Panel settingsPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly System.Windows.Forms.Timer cardPulse = new() { Interval = 60 };
    private readonly TextBox groupId = new() { MaxLength = 4, CharacterCasing = CharacterCasing.Upper, Width = 90 };
    private readonly TextBox groupConfirmation = new() { MaxLength = 4, CharacterCasing = CharacterCasing.Upper, Width = 90 };
    private readonly CheckBox applyCompanionGroup = new() { Text = "This companion", Checked = true, AutoSize = true };
    private readonly CheckBox applyActiveGroup = new() { Text = "All active devices, sequentially", AutoSize = true };
    private readonly Button applyGroup = new() { Text = "Change group ID", BackColor = Color.FromArgb(178, 34, 34), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Enabled = false, AutoSize = true };
    private readonly Label diagnosticsCounters = new() { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private readonly CheckBox localMute = new() { Text = "Mute my speaker", AutoSize = true };
    private readonly Label identityMicrophone = new() { AutoEllipsis = true };
    private readonly Label identitySpeaker = new() { AutoEllipsis = true };
    private IReadOnlyList<Peer> displayPeers = [];
    private string activeFilter = "All";
    private float pulsePhase;
    private NotifyIcon? trayIcon;
    private bool allowExit;

    private void BuildRedesign()
    {
        Controls.Clear();
        Text = "Wi-Fi Intercom Companion";
        MinimumSize = new Size(1024, 700);
        Size = new Size(1280, 820);
        BackColor = Color.FromArgb(245, 246, 247);

        var appBar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.White, Padding = new Padding(14, 0, 14, 0) };
        appBar.Controls.Add(new Label { Text = "▦  Wi-Fi Intercom Companion", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Regular), Location = new Point(14, 10) });
        var identityStrip = new TableLayoutPanel { Dock = DockStyle.Top, Height = 56, BackColor = Color.White, Padding = new Padding(20, 4, 14, 4), ColumnCount = 6 };
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 278));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        identityLabel.AutoSize = false;
        identityLabel.Dock = DockStyle.Fill;
        identityLabel.Font = new Font("Segoe UI", 8.5f);
        identityLabel.TextAlign = ContentAlignment.MiddleLeft;
        foreach (var label in new[] { identityMicrophone, identitySpeaker })
        {
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.Font = new Font("Segoe UI", 8.5f);
            label.ForeColor = Color.FromArgb(99, 103, 109);
            label.TextAlign = ContentAlignment.MiddleLeft;
        }
        localMute.Appearance = Appearance.Button;
        localMute.AutoSize = false;
        localMute.Width = 118;
        localMute.Height = 32;
        localMute.TextAlign = ContentAlignment.MiddleCenter;
        localMute.FlatStyle = FlatStyle.Flat;
        localMute.FlatAppearance.BorderColor = Color.FromArgb(173, 178, 184);
        localMute.CheckedChanged += (_, _) => audio?.SetLocalPlaybackMuted(localMute.Checked);
        var settingsButton = PlainButton("Settings…");
        settingsButton.Height = 32;
        settingsButton.Margin = new Padding(8, 0, 0, 0);
        settingsButton.Click += (_, _) => ShowSettings("USB");
        identityStrip.Controls.Add(identityLabel, 0, 0);
        identityStrip.Controls.Add(identityMicrophone, 1, 0);
        identityStrip.Controls.Add(identitySpeaker, 2, 0);
        identityStrip.Controls.Add(localMute, 4, 0);
        identityStrip.Controls.Add(settingsButton, 5, 0);

        var nowBar = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.FromArgb(232, 243, 251), Padding = new Padding(20, 11, 16, 7) };
        statusLabel.Location = new Point(20, 9);
        statusLabel.AutoSize = true;
        statusLabel.Visible = true;
        nowDetail.Location = new Point(22, 45);
        nowDetail.Visible = true;
        nowBar.Controls.AddRange([statusLabel, nowDetail]);
        statusLabel.BringToFront();
        nowDetail.BringToFront();

        BuildTalkPage();
        BuildSettingsPage();
        pageHost.Controls.Add(settingsPage);
        pageHost.Controls.Add(talkPage);
        Controls.Add(pageHost);
        Controls.Add(nowBar);
        Controls.Add(identityStrip);
        Controls.Add(appBar);

        deviceFilter.TextChanged += (_, _) => RefreshRedesignPeers();
        cardPulse.Tick += (_, _) => PulseCards();
        cardPulse.Start();
        UpdateIdentityPresentation();
    }

    private void BuildTalkPage()
    {
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.FromArgb(245, 246, 247) };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 76));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));

        var devicesPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 8, 12) };
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false };
        header.Controls.Add(new Label { Text = "Devices", Font = new Font("Segoe UI", 11, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 6, 12, 0) });
        header.Controls.Add(deviceCount);
        header.Controls.Add(deviceFilter);
        foreach (var name in new[] { "All", "Speaking", "Muted", "Silenced", "Legacy" })
        {
            var chip = PlainButton(name);
            chip.Margin = new Padding(4, 0, 0, 0);
            chip.Click += (_, _) => { activeFilter = name; RefreshRedesignPeers(); };
            header.Controls.Add(chip);
        }
        var offlineContainer = new Panel { Dock = DockStyle.Bottom, Height = 152, AutoScroll = true, BackColor = Color.White };
        offlineContainer.Controls.Add(offlineDevices);
        devicesPanel.Controls.Add(deviceGrid);
        devicesPanel.Controls.Add(offlineContainer);
        devicesPanel.Controls.Add(header);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 12, 14, 12), BackColor = Color.White };
        var pttPanel = new Panel { Dock = DockStyle.Top, Height = 142 };
        broadcast.SetBounds(8, 0, 0, 68);
        broadcast.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        reply.SetBounds(8, 88, 0, 56);
        reply.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        pttPanel.Resize += (_, _) => { broadcast.Width = pttPanel.ClientSize.Width - 8; reply.Width = pttPanel.ClientSize.Width - 8; };
        pttPanel.Controls.AddRange([broadcast, reply]);
        var activityTitle = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.White, Padding = new Padding(8, 12, 8, 4) };
        activityTitle.Controls.Add(new Label { Text = "Activity", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(0, 4) });
        var diagnostics = new LinkLabel { Text = "Diagnostics…", AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 4) };
        diagnostics.Click += (_, _) => ShowSettings("Diagnostics");
        var recordings = new LinkLabel { Text = "Open recordings folder", AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 4, 0, 8) };
        recordings.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CompanionSettings.RecordingsDirectory) { UseShellExecute = true }); } catch { } };
        right.Controls.Add(activity);
        right.Controls.Add(diagnostics);
        right.Controls.Add(recordings);
        right.Controls.Add(activityTitle);
        right.Controls.Add(pttPanel);
        split.Controls.Add(devicesPanel, 0, 0);
        split.Controls.Add(right, 1, 0);
        talkPage.Controls.Add(split);
    }

    private void BuildSettingsPage()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 212, IsSplitterFixed = true };
        split.Panel1.BackColor = Color.FromArgb(238, 240, 242);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 18, 12, 12) };
        nav.Controls.Add(new Label { Text = "SETTINGS", ForeColor = Color.FromArgb(99, 103, 109), AutoSize = true, Padding = new Padding(4, 0, 0, 8) });
        foreach (var (title, page) in new[] { ("Set up a device (USB)", "USB"), ("Group and device IDs", "Group"), ("Firmware", "Firmware"), ("Identity, audio, shortcuts", "Identity"), ("Diagnostics", "Diagnostics") })
        {
            var button = PlainButton(title);
            button.Width = 184;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Click += (_, _) => ShowSettings(page);
            nav.Controls.Add(button);
        }
        var back = PlainButton("← Back to Talk");
        back.Width = 184;
        back.Margin = new Padding(3, 18, 3, 3);
        back.Click += (_, _) => ShowTalk();
        nav.Controls.Add(back);
        split.Panel1.Controls.Add(nav);
        split.Panel2.Controls.Add(BuildSettingsDetail());
        settingsPage.Controls.Add(split);
    }

    private Control BuildSettingsDetail()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(1, 1), SizeMode = TabSizeMode.Fixed };
        tabs.TabPages.Add(BuildUsbPage());
        tabs.TabPages.Add(BuildGroupPage());
        tabs.TabPages.Add(BuildFirmwarePage());
        tabs.TabPages.Add(BuildIdentityPage());
        tabs.TabPages.Add(BuildDiagnosticsPage());
        tabs.Name = "SettingsTabs";
        return tabs;
    }

    private TabPage Page(string name, string title, string detail)
    {
        var page = new TabPage { Name = name, BackColor = Color.FromArgb(245, 246, 247), Padding = new Padding(28, 22, 28, 20) };
        page.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold), Location = new Point(28, 22) });
        page.Controls.Add(new Label { Text = detail, AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.FromArgb(99, 103, 109), Location = new Point(30, 56) });
        return page;
    }

    private TabPage BuildUsbPage()
    {
        var page = Page("USB", "Set up a device over USB", "Connect the device by USB, enter Wi-Fi credentials, and reboot it onto the network. Passwords are never saved by the companion.");
        var panel = new FlowLayoutPanel { Location = new Point(28, 110), AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var port = new FlowLayoutPanel { AutoSize = true }; port.Controls.AddRange([new Label { Text = "USB port", Width = 130, Padding = new Padding(0, 7, 0, 0) }, usbPort, refreshUsbPorts, getUsbConfig]);
        var wifi = new FlowLayoutPanel { AutoSize = true }; wifi.Controls.AddRange([new Label { Text = "Wi-Fi SSID", Width = 130, Padding = new Padding(0, 7, 0, 0) }, usbSsid]);
        var password = new FlowLayoutPanel { AutoSize = true }; password.Controls.AddRange([new Label { Text = "Password", Width = 130, Padding = new Padding(0, 7, 0, 0) }, usbPassword]);
        panel.Controls.AddRange([port, wifi, password, applyUsbWifi, usbStatus]);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildGroupPage()
    {
        var page = Page("Group", "Group and device IDs", "A group is an intercom channel, not a radio network. A group holds up to 16 devices on the same trusted LAN.");
        var current = new Label { AutoSize = true, Location = new Point(30, 105) };
        current.Name = "CurrentGroup";
        var danger = new Label { Text = "Changing a group immediately separates this companion from the old group. Devices reboot after acknowledging the update. Verify the target group carefully; there is no remote recovery across groups.", ForeColor = Color.FromArgb(178, 34, 34), MaximumSize = new Size(680, 0), AutoSize = true, Location = new Point(30, 145) };
        var fields = new FlowLayoutPanel { Location = new Point(30, 225), AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        fields.Controls.Add(Labeled("New group (4 A–Z/0–9)", groupId));
        fields.Controls.Add(Labeled("Type current group to confirm", groupConfirmation));
        fields.Controls.Add(applyCompanionGroup);
        fields.Controls.Add(applyActiveGroup);
        fields.Controls.Add(applyGroup);
        page.Controls.AddRange([current, danger, fields]);
        groupId.TextChanged += (_, _) => UpdateGroupButton();
        groupConfirmation.TextChanged += (_, _) => UpdateGroupButton();
        applyGroup.Click += async (_, _) => await ApplyGroupChangeAsync();
        return page;
    }

    private TabPage BuildFirmwarePage()
    {
        var page = Page("Firmware", "Firmware", "Signed packages only. Updates are queued one device at a time and complete only after the device re-announces the offered version.");
        var panel = new FlowLayoutPanel { Location = new Point(28, 110), AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.AddRange([browseOtaManifest, otaManifest, updateSelectedDevice, updateAllDevices, otaStatus]);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildIdentityPage()
    {
        var page = Page("Identity", "Identity, audio and shortcuts", "Changes to audio devices are applied independently from discovery and the device grid.");
        var panel = new FlowLayoutPanel { Location = new Point(28, 110), AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(Labeled("Companion alias", companionAlias));
        panel.Controls.Add(saveCompanionAlias);
        panel.Controls.Add(Labeled("Microphone", recordingDevice));
        panel.Controls.Add(Labeled("Speaker", playbackDevice));
        panel.Controls.Add(applyAudioDevices);
        var runInTray = new CheckBox { Text = "Run in notification area when closed", Checked = settings.RunInNotificationArea, AutoSize = true };
        runInTray.CheckedChanged += (_, _) => { settings.RunInNotificationArea = runInTray.Checked; settings.Save(); };
        panel.Controls.Add(runInTray);
        panel.Controls.Add(new Label { Text = "Space = broadcast while the companion is focused.", AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109) });
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildDiagnosticsPage()
    {
        var page = Page("Diagnostics", "Diagnostics", "Live receive counters and the discovery/session log. Audio availability never controls discovery.");
        diagnosticsCounters.Location = new Point(30, 110);
        var copy = PlainButton("Copy log");
        copy.Location = new Point(30, 150);
        copy.Click += (_, _) => { try { Clipboard.SetText(ReadDiagnosticLog()); } catch { } };
        var save = PlainButton("Save log to file…");
        save.Location = new Point(122, 150);
        save.Click += (_, _) => SaveDiagnosticLog();
        page.Controls.AddRange([diagnosticsCounters, copy, save]);
        return page;
    }

    private void ShowSettings(string page)
    {
        talkPage.Visible = false;
        settingsPage.Visible = true;
        var tabs = settingsPage.Controls.Find("SettingsTabs", true).OfType<TabControl>().FirstOrDefault();
        if (tabs is not null) tabs.SelectedIndex = page switch { "USB" => 0, "Group" => 1, "Firmware" => 2, "Identity" => 3, _ => 4 };
        UpdateSettingsStatus();
    }

    private void ShowTalk()
    {
        settingsPage.Visible = false;
        talkPage.Visible = true;
    }

    private void RefreshRedesignPeers(IReadOnlyList<Peer>? peers = null)
    {
        if (peers is not null) displayPeers = peers;
        var search = deviceFilter.Text.Trim();
        var active = displayPeers.Where(peer => (string.IsNullOrEmpty(search) || peer.Alias.Contains(search, StringComparison.OrdinalIgnoreCase) || peer.NodeId.ToString("x8").Contains(search, StringComparison.OrdinalIgnoreCase)) && MatchesActiveFilter(peer)).OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
        deviceCount.Text = $"{displayPeers.Count} of 16 in group {settings.MeshId}";
        UpdateIdentityPresentation();
        var activeIds = displayPeers.Select(peer => peer.NodeId).ToHashSet();
        foreach (var peer in displayPeers)
        {
            if (!rememberedAt.TryGetValue(peer.NodeId, out var seen) || seen != peer.LastSeen) { knownDevices.Remember(peer, configurations.GetValueOrDefault(peer.NodeId)); rememberedAt[peer.NodeId] = peer.LastSeen; }
            if (!deviceCards.TryGetValue(peer.NodeId, out var card))
            {
                card = new DeviceCard();
                card.TalkPressed += target => { selectedDeviceId = target.NodeId; if (!otaUpdating) receiveSession?.PressSelected(target); };
                card.TalkReleased += () => receiveSession?.ReleasePtt();
                card.SilenceClicked += async target => await ToggleSoftMuteAsync(target);
                card.MoreClicked += target => ShowDeviceMenu(card, target);
                card.VolumeCommitted += async (target, value) => await CommitVolumeAsync(target, value);
                deviceCards.Add(peer.NodeId, card);
                deviceGrid.Controls.Add(card);
            }
            var volumeValue = configurations.GetValueOrDefault(peer.NodeId)?.SpeakerVolume ?? knownDevices.Devices.FirstOrDefault(device => device.NodeId == peer.NodeId)?.SpeakerVolume ?? 512;
            card.UpdatePeer(peer, volumeValue, otaUpdating);
            card.Visible = active.Contains(peer);
        }
        foreach (var stale in deviceCards.Keys.Except(activeIds).ToArray()) { deviceGrid.Controls.Remove(deviceCards[stale]); deviceCards[stale].Dispose(); deviceCards.Remove(stale); }
        RefreshOfflineRows(activeIds);
        UpdateSettingsStatus();
    }

    private void RefreshOfflineRows(ISet<uint> activeIds)
    {
        offlineDevices.SuspendLayout();
        offlineDevices.Controls.Clear();
        var offline = knownDevices.Devices.Where(device => !activeIds.Contains(device.NodeId)).OrderBy(device => device.Alias).ToArray();
        offlineDevices.Controls.Add(new Label { Text = offline.Length == 0 ? "Known, not responding — none" : $"Known, not responding — {offline.Length}", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        foreach (var device in offline)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Width = 650, WrapContents = false };
            row.Controls.Add(new Label { Text = $"{device.Alias}  ·  {device.NodeId:x8}  ·  {device.LastAddress}  ·  last seen {(DateTimeOffset.Now - device.LastSeen):g} ago", AutoSize = true, Width = 520, ForeColor = Color.FromArgb(99, 103, 109) });
            var remove = PlainButton("Remove…"); remove.Click += (_, _) => RemoveKnownDevice(device); row.Controls.Add(remove); offlineDevices.Controls.Add(row);
        }
        if (offline.Length > 0) { var all = PlainButton($"Remove all {offline.Length}…"); all.Click += (_, _) => RemoveAllOffline(activeIds, offline.Length); offlineDevices.Controls.Add(all); }
        offlineDevices.ResumeLayout();
    }

    private bool MatchesActiveFilter(Peer peer) => activeFilter switch
    {
        "Speaking" => peer.IsTalking,
        "Muted" => peer.HardwareMuted,
        "Silenced" => peer.SoftMuted,
        "Legacy" => peer.ProtocolVersion is null or 1,
        _ => true,
    };

    private void PulseCards()
    {
        pulsePhase += 0.18f;
        foreach (var card in deviceCards.Values) card.SetPulse(pulsePhase);
        if (receiveSession?.State == IntercomState.Receiving && audio is not null)
        {
            var statistics = receiveSession.Statistics;
            nowDetail.Text = $"UDP {statistics.AudioPackets:n0} · decoded {statistics.DecodedFrames:n0} · PLC {statistics.ConcealedFrames:n0} · output {audio.BufferedMilliseconds} ms";
        }
        diagnosticsCounters.Text = nowDetail.Text;
    }

    private async Task ToggleSoftMuteAsync(Peer peer)
    {
        if (node is null || !peer.SupportsMuteReporting || peer.HardwareMuted || otaUpdating) return;
        try
        {
            var current = await node.RequestConfigurationAsync(peer);
            configurations[peer.NodeId] = current.Configuration;
            var next = current.Configuration with { SoftMute = !current.Configuration.SoftMute };
            var reply = await node.SetConfigurationAsync(peer, next);
            configurations[peer.NodeId] = reply.Configuration;
            knownDevices.Remember(peer, reply.Configuration);
            RefreshRedesignPeers();
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException) { nowDetail.Text = $"Silence failed: {exception.Message}"; }
    }

    private async Task CommitVolumeAsync(Peer peer, int value)
    {
        if (node is null || otaUpdating) return;
        try
        {
            var current = configurations.GetValueOrDefault(peer.NodeId);
            current ??= (await node.RequestConfigurationAsync(peer)).Configuration;
            var reply = await node.SetConfigurationAsync(peer, current with { SpeakerVolume = value });
            configurations[peer.NodeId] = reply.Configuration;
            knownDevices.Remember(peer, reply.Configuration);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException) { nowDetail.Text = $"Volume update failed: {exception.Message}"; }
    }

    private void ShowDeviceMenu(Control card, Peer peer)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Configure…", null, async (_, _) => await ConfigurePeerAsync(peer));
        menu.Items.Add("Get configuration", null, async (_, _) => await GetPeerConfigurationAsync(peer));
        menu.Items.Add("Update firmware…", null, async (_, _) => { selectedDeviceId = peer.NodeId; ShowSettings("Firmware"); await StartOtaUpdateAsync(false); });
        menu.Items.Add("Remove", null, (_, _) => RemoveKnownDevice(new KnownDevice(peer.NodeId, peer.Alias, peer.Endpoint.Address.ToString(), peer.LastSeen, null, null, peer.SoftMuted)));
        menu.Show(card, new Point(card.Width - 28, 65));
    }

    private async Task GetPeerConfigurationAsync(Peer peer)
    {
        if (node is null) return;
        try { var reply = await node.RequestConfigurationAsync(peer); configurations[peer.NodeId] = reply.Configuration; knownDevices.Remember(peer, reply.Configuration); RefreshRedesignPeers(); }
        catch (Exception exception) when (exception is SocketException or TimeoutException) { nowDetail.Text = $"Configuration read failed: {exception.Message}"; }
    }

    private async Task ConfigurePeerAsync(Peer peer)
    {
        if (node is null) return;
        try
        {
            var current = (await node.RequestConfigurationAsync(peer)).Configuration;
            configurations[peer.NodeId] = current;
            using var dialog = new Form { Text = $"Configure {peer.Alias}", ClientSize = new Size(440, 350), StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 9), MinimizeBox = false, MaximizeBox = false };
            var aliasBox = new TextBox { Text = current.Alias, Width = 220 };
            var volumeBox = new NumericUpDown { Minimum = 64, Maximum = 1024, Value = Math.Clamp(current.SpeakerVolume, 64, 1024), Width = 100 };
            var brightnessBox = new NumericUpDown { Minimum = 0, Maximum = 255, Value = Math.Clamp(current.LedBrightness, 0, 255), Width = 100 };
            var softMuteBox = new CheckBox { Text = current.HardwareMuted ? "Physical mute slider is on" : "Soft mute playback", Checked = current.SoftMute, Enabled = !current.HardwareMuted, AutoSize = true };
            var swapped = new CheckBox { Text = "Buttons swapped", Checked = current.ButtonsSwapped, AutoSize = true };
            var orientationBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 }; orientationBox.Items.AddRange(["0", "180"]); orientationBox.SelectedItem = current.RingOrientation.ToString();
            var deviceId = new NumericUpDown { Minimum = 1, Maximum = uint.MaxValue, Value = current.DeviceId, Width = 140 };
            var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20) };
            fields.Controls.Add(new Label { Text = "Edits remain local until Apply to device. Group, device ID, brightness, buttons and orientation restart after acknowledgement.", AutoSize = true, MaximumSize = new Size(390, 0), ForeColor = Color.FromArgb(99, 103, 109) });
            fields.Controls.Add(Labeled("Alias", aliasBox)); fields.Controls.Add(Labeled("Speaker volume", volumeBox)); fields.Controls.Add(Labeled("Ring brightness", brightnessBox)); fields.Controls.Add(softMuteBox); fields.Controls.Add(swapped); fields.Controls.Add(Labeled("Ring centre", orientationBox)); fields.Controls.Add(Labeled("Device ID", deviceId));
            var apply = new Button { Text = "Apply to device", DialogResult = DialogResult.OK, BackColor = Color.FromArgb(21, 101, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, AutoSize = true };
            fields.Controls.Add(apply); dialog.AcceptButton = apply; dialog.Controls.Add(fields);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var requestedId = checked((uint)deviceId.Value);
            if (requestedId != peer.NodeId && node.Peers.Any(other => other.NodeId != peer.NodeId && other.NodeId == requestedId)) { MessageBox.Show(this, "That device ID is already held by a visible device. Nothing was sent.", "Duplicate device ID", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var next = current with { Alias = aliasBox.Text.Trim(), SpeakerVolume = (int)volumeBox.Value, LedBrightness = (int)brightnessBox.Value, SoftMute = softMuteBox.Checked, ButtonsSwapped = swapped.Checked, RingOrientation = int.Parse(orientationBox.Text), DeviceId = requestedId };
            var reply = await node.SetConfigurationAsync(peer, next);
            configurations[peer.NodeId] = reply.Configuration; knownDevices.Remember(peer, reply.Configuration); RefreshRedesignPeers();
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException) { nowDetail.Text = $"Configuration update failed: {exception.Message}"; }
    }

    private void RemoveKnownDevice(KnownDevice device)
    {
        if (MessageBox.Show(this, $"Forget {device.Alias} on this companion? This only removes this PC's local record; it does not change the device or remove it from its group.", "Remove device", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        knownDevices.Remove(device.NodeId); RefreshRedesignPeers();
    }

    private void RemoveAllOffline(ISet<uint> activeIds, int count)
    {
        if (MessageBox.Show(this, $"Forget {count} offline device record(s) on this companion?", "Remove offline devices", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        knownDevices.RemoveAllOffline(activeIds); RefreshRedesignPeers();
    }

    private void UpdateSettingsStatus()
    {
        var label = settingsPage.Controls.Find("CurrentGroup", true).OfType<Label>().FirstOrDefault();
        if (label is not null) label.Text = $"Current group: {settings.MeshId} · {displayPeers.Count} active device(s) · companion ID {settings.NodeId:x8}";
        UpdateGroupButton();
    }

    private void UpdateIdentityPresentation()
    {
        identityLabel.Text = $"{settings.Alias.ToUpperInvariant()}\nID {settings.NodeId:x8} · group {settings.MeshId} · {displayPeers.Count} of 16 devices";
        identityMicrophone.Text = $"MIC\n{TrimDeviceName((recordingDevice.SelectedItem as RecordingDevice)?.Name ?? "Not selected")}";
        identitySpeaker.Text = $"SPEAKER\n{TrimDeviceName((playbackDevice.SelectedItem as PlaybackDevice)?.Name ?? "Not selected")}";
    }

    private static string TrimDeviceName(string value) => value.Length <= 23 ? value : $"{value[..20]}…";

    private void UpdateGroupButton() => applyGroup.Enabled = Protocol.IsValidMeshId(groupId.Text) && groupConfirmation.Text == settings.MeshId && (applyCompanionGroup.Checked || applyActiveGroup.Checked);

    private async Task ApplyGroupChangeAsync()
    {
        if (node is null || !applyGroup.Enabled) return;
        var targetGroup = groupId.Text;
        var targets = applyActiveGroup.Checked ? node.SnapshotActivePeers().Where(peer => peer.ProtocolVersion is >= 2).ToArray() : [];
        applyGroup.Enabled = false;
        foreach (var peer in targets)
        {
            try { var current = (await node.RequestConfigurationAsync(peer)).Configuration; await node.SetConfigurationAsync(peer, current with { MeshId = targetGroup }); RecordActivity($"Group update queued for {peer.Alias}."); }
            catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException) { RecordActivity($"Group update failed for {peer.Alias}: {exception.Message}"); }
        }
        if (applyCompanionGroup.Checked) node.SetMeshId(targetGroup);
        groupConfirmation.Clear(); groupId.Clear(); RefreshRedesignPeers();
    }

    private void RecordActivity(string text)
    {
        activity.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (activity.Items.Count > 120) activity.Items.RemoveAt(activity.Items.Count - 1);
    }

    private static FlowLayoutPanel Labeled(string label, Control field)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        row.Controls.Add(new Label { Text = label, Width = 190, Padding = new Padding(0, 6, 0, 0) }); row.Controls.Add(field); return row;
    }

    private static Button PlainButton(string text) => new() { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(23, 25, 28), Margin = new Padding(3), Padding = new Padding(6, 4, 6, 4) };

    private static string ReadDiagnosticLog()
    {
        try { return File.ReadAllText(Path.Combine(CompanionSettings.AppDataDirectory, "diagnostics.log")); } catch (IOException) { return "No diagnostic log is available."; }
    }

    private void SaveDiagnosticLog()
    {
        using var dialog = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt", FileName = "wifi-intercom-diagnostics.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, ReadDiagnosticLog()); } catch (IOException exception) { MessageBox.Show(this, exception.Message, "Could not save log", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void EnableTray()
    {
        if (trayIcon is null)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("Exit", null, (_, _) => { allowExit = true; Close(); });
            trayIcon = new NotifyIcon { Text = "Wi-Fi Intercom Companion", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
            trayIcon.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        }
    }

    private bool KeepRunningInTray(FormClosingEventArgs eventArgs)
    {
        if (!allowExit && eventArgs.CloseReason == CloseReason.UserClosing && settings.RunInNotificationArea)
        {
            eventArgs.Cancel = true;
            Hide();
            trayIcon?.ShowBalloonTip(1000, "Wi-Fi Intercom", "Still running in the notification area; hotkeys remain active.", ToolTipIcon.Info);
            return true;
        }
        if (trayIcon is not null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
        return false;
    }

    private bool ConfirmOtaUpdate(OtaPackage package, IReadOnlyList<Peer> targets)
    {
        using var dialog = new Form { Text = "Confirm firmware update", ClientSize = new Size(500, 310), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var acknowledgement = new CheckBox { Text = "I understand that each device reboots and is unavailable during its update.", AutoSize = true, MaximumSize = new Size(455, 0), Location = new Point(22, 200) };
        var start = new Button { Text = "Start update", DialogResult = DialogResult.OK, Enabled = false, BackColor = Color.FromArgb(21, 101, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Location = new Point(275, 255), AutoSize = true };
        acknowledgement.CheckedChanged += (_, _) => start.Enabled = acknowledgement.Checked;
        dialog.Controls.AddRange([
            new Label { Text = $"Update {targets.Count} device(s) to {package.Version}?", AutoSize = true, Font = new Font("Segoe UI", 13, FontStyle.Bold), Location = new Point(22, 20) },
            new Label { Text = $"Package: {Path.GetFileName(package.ManifestPath)}\n\nDevices update one at a time: {string.Join(", ", targets.Select(peer => peer.Alias))}\n\nKeep the companion open and awake. A firewall prompt may be required for the temporary private-LAN firmware server. A failed candidate rolls back and the queue continues.", AutoSize = true, MaximumSize = new Size(455, 0), Location = new Point(22, 58), ForeColor = Color.FromArgb(99, 103, 109) }, acknowledgement, start,
            new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(365, 255), AutoSize = true }
        ]);
        dialog.AcceptButton = start;
        return dialog.ShowDialog(this) == DialogResult.OK && acknowledgement.Checked;
    }
}
