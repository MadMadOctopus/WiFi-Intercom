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
    private readonly Label nowKicker = new() { AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109), Font = new Font("Segoe UI", 8, FontStyle.Regular) };
    private readonly Label nowTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold) };
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
    private readonly Label usbDeviceIdentity = new() { AutoSize = true, ForeColor = Color.FromArgb(79, 91, 102) };
    private readonly FlowLayoutPanel settingsNavigation = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Dictionary<string, Panel> settingsNavItems = [];
    private readonly FlowLayoutPanel otaQueue = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly HashSet<uint> queuedOtaDevices = [];
    private string activeSettingsPage = "USB";
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

        // The application name belongs in the native Windows title bar.  This strip is
        // deliberately only the companion's identity and local audio state.
        var identityStrip = new TableLayoutPanel { Dock = DockStyle.Top, Height = 56, BackColor = Color.White, Padding = new Padding(20, 4, 20, 4), ColumnCount = 7 };
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
        localMute.Width = 122;
        localMute.Height = 30;
        localMute.TextAlign = ContentAlignment.MiddleCenter;
        localMute.FlatStyle = FlatStyle.Flat;
        localMute.FlatAppearance.BorderColor = Color.FromArgb(173, 178, 184);
        localMute.CheckedChanged += (_, _) => audio?.SetLocalPlaybackMuted(localMute.Checked);
        var changeAudio = new LinkLabel { Text = "Change…", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 0, 12, 0), Font = new Font("Segoe UI", 9) };
        changeAudio.Click += (_, _) => ShowSettings("Identity");
        var settingsButton = PlainButton("Settings…");
        settingsButton.Height = 30;
        settingsButton.Margin = new Padding(8, 0, 0, 0);
        settingsButton.Click += (_, _) => ShowSettings("USB");
        identityStrip.Controls.Add(identityLabel, 0, 0);
        identityStrip.Controls.Add(identityMicrophone, 1, 0);
        identityStrip.Controls.Add(identitySpeaker, 2, 0);
        identityStrip.Controls.Add(changeAudio, 3, 0);
        identityStrip.Controls.Add(localMute, 5, 0);
        identityStrip.Controls.Add(settingsButton, 6, 0);
        identityStrip.Paint += (_, eventArgs) => eventArgs.Graphics.DrawLine(new Pen(Color.FromArgb(222, 225, 229)), 0, identityStrip.Height - 1, identityStrip.Width, identityStrip.Height - 1);

        var nowBar = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.FromArgb(232, 243, 251), Padding = new Padding(20, 0, 20, 0) };
        var nowAccent = new Panel { Location = new Point(20, 16), Size = new Size(4, 44), BackColor = Color.FromArgb(46, 125, 50), Anchor = AnchorStyles.Left | AnchorStyles.Top };
        // statusLabel remains the state source for the original audio code;
        // nowTitle is the dedicated visual owner for this redesigned strip.
        statusLabel.Visible = false;
        nowTitle.Location = new Point(40, 29);
        nowTitle.Text = "Idle";
        nowTitle.ForeColor = Color.FromArgb(46, 125, 50);
        nowKicker.Location = new Point(40, 14);
        nowKicker.Text = "READY";
        nowDetail.AutoSize = false;
        nowDetail.Size = new Size(360, 44);
        nowDetail.Location = new Point(Math.Max(440, nowBar.Width - 380), 16);
        nowDetail.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        nowDetail.TextAlign = ContentAlignment.MiddleRight;
        nowDetail.Visible = true;
        nowBar.Controls.AddRange([nowAccent, nowKicker, nowTitle, nowDetail]);
        nowBar.Paint += (_, _) => nowAccent.BackColor = nowTitle.ForeColor;
        nowBar.Paint += (_, eventArgs) => eventArgs.Graphics.DrawLine(new Pen(Color.FromArgb(202, 220, 232)), 0, nowBar.Height - 1, nowBar.Width, nowBar.Height - 1);
        nowTitle.BringToFront();
        nowDetail.BringToFront();

        BuildTalkPage();
        BuildSettingsPage();
        pageHost.Controls.Add(settingsPage);
        pageHost.Controls.Add(talkPage);
        Controls.Add(pageHost);
        Controls.Add(nowBar);
        Controls.Add(identityStrip);

        deviceFilter.TextChanged += (_, _) => RefreshRedesignPeers();
        cardPulse.Tick += (_, _) => PulseCards();
        cardPulse.Start();
        UpdateIdentityPresentation();
    }

    private void BuildTalkPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = Color.FromArgb(245, 246, 247) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(28, 10, 20, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "Devices", Font = new Font("Segoe UI", 11, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        deviceCount.Anchor = AnchorStyles.Left;
        header.Controls.Add(deviceCount, 1, 0);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false, FlowDirection = FlowDirection.LeftToRight };
        foreach (var name in new[] { "All", "Speaking", "Muted", "Silenced", "Legacy" })
        {
            var chip = Chip(name);
            chip.Click += (_, _) => { activeFilter = name; RefreshRedesignPeers(); };
            filters.Controls.Add(chip);
        }
        deviceFilter.Anchor = AnchorStyles.Left;
        header.Controls.Add(deviceFilter, 2, 0);
        filters.Anchor = AnchorStyles.Right;
        header.Controls.Add(filters, 3, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(28, 0, 20, 10) };
        // 1280px is the reference layout: this keeps three 296px device cards
        // aligned in the grid instead of letting the action column force a wrap.
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 79));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
        var deviceSurface = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(245, 246, 247) };
        deviceSurface.Controls.Add(deviceGrid);
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0), BackColor = Color.FromArgb(245, 246, 247) };
        var pttPanel = new Panel { Dock = DockStyle.Top, Height = 214, Padding = new Padding(16, 14, 16, 0), BackColor = Color.White };
        pttPanel.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(220, 223, 227)), 0, 0, pttPanel.Width - 1, pttPanel.Height - 1);
        broadcast.AutoSize = replySurface.AutoSize = false;
        broadcast.SetBounds(16, 14, 260, 62);
        replySurface.SetBounds(16, 116, 260, 44);
        broadcast.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        replySurface.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        pttPanel.Resize += (_, _) =>
        {
            broadcast.Width = Math.Max(1, pttPanel.ClientSize.Width - 32);
            replySurface.Width = Math.Max(1, pttPanel.ClientSize.Width - 32);
        };
        var broadcastHint = new Label { Text = $"Everyone in group {settings.MeshId}                         Space / Ctrl+Alt+B", AutoEllipsis = true, ForeColor = Color.FromArgb(99, 103, 109), Font = new Font("Segoe UI", 7.5f), Location = new Point(16, 90), Size = new Size(260, 14), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        var replyHint = new Label { Text = "Last sender: none                                         Ctrl+Alt+R", AutoEllipsis = true, ForeColor = Color.FromArgb(99, 103, 109), Font = new Font("Segoe UI", 7.5f), Location = new Point(16, 169), Size = new Size(260, 14), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        replySurface.AutoSize = false;
        replySurface.UseVisualStyleBackColor = false;
        replySurface.BackColor = Color.FromArgb(21, 101, 192);
        replySurface.ForeColor = Color.White;
        replySurface.FlatAppearance.BorderColor = Color.FromArgb(21, 101, 192);
        pttPanel.Controls.AddRange([broadcast, broadcastHint, replySurface, replyHint]);
        var gap = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Color.FromArgb(245, 246, 247) };
        var activityPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        activityPanel.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(220, 223, 227)), 0, 0, activityPanel.Width - 1, activityPanel.Height - 1);
        var activityTitle = new Label { Text = "Activity", Dock = DockStyle.Top, Height = 42, Padding = new Padding(16, 13, 0, 0), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        var diagnostics = new LinkLabel { Text = "Diagnostics…", AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(16, 10, 0, 10) };
        diagnostics.Click += (_, _) => ShowSettings("Diagnostics");
        var recordings = new LinkLabel { Text = "Open recordings folder", AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(16, 4, 0, 10) };
        recordings.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CompanionSettings.RecordingsDirectory) { UseShellExecute = true }); } catch { } };
        activityPanel.Controls.Add(activity);
        activityPanel.Controls.Add(diagnostics);
        activityPanel.Controls.Add(recordings);
        activityPanel.Controls.Add(activityTitle);
        right.Controls.Add(activityPanel);
        right.Controls.Add(gap);
        right.Controls.Add(pttPanel);
        content.Controls.Add(deviceSurface, 0, 0);
        content.Controls.Add(right, 1, 0);
        var offlineContainer = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(18, 10, 18, 8) };
        offlineContainer.Controls.Add(offlineDevices);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(content, 0, 1);
        layout.Controls.Add(offlineContainer, 0, 2);
        talkPage.Controls.Add(layout);
    }

    private void BuildSettingsPage()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 238, IsSplitterFixed = true };
        split.Panel1.BackColor = Color.FromArgb(240, 241, 243);
        settingsNavigation.Padding = new Padding(0, 18, 0, 0);
        settingsNavigation.Controls.Add(new Label { Text = "SETTINGS", ForeColor = Color.FromArgb(99, 103, 109), AutoSize = true, Padding = new Padding(18, 0, 0, 12), Font = new Font("Segoe UI", 8, FontStyle.Regular) });
        foreach (var (title, page) in new[] { ("Set up a device (USB)", "USB"), ("Group and device IDs", "Group"), ("Firmware", "Firmware"), ("Identity, audio, shortcuts", "Identity"), ("Diagnostics", "Diagnostics") })
        {
            var item = SettingsNavItem(title, page);
            settingsNavItems.Add(page, item);
            settingsNavigation.Controls.Add(item);
        }
        split.Panel1.Controls.Add(settingsNavigation);
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
        var page = new TabPage { Name = name, BackColor = Color.FromArgb(245, 246, 247), Padding = new Padding(30, 24, 30, 20) };
        page.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold), Location = new Point(30, 24) });
        page.Controls.Add(new Label { Text = detail, AutoSize = true, MaximumSize = new Size(920, 0), ForeColor = Color.FromArgb(99, 103, 109), Location = new Point(30, 58) });
        return page;
    }

    private TabPage BuildUsbPage()
    {
        var page = Page("USB", "Set up a device over USB", "Connect the device by USB, enter Wi-Fi credentials, and reboot it onto the network. Passwords are never saved by the companion.");
        var steps = new TableLayoutPanel { Location = new Point(30, 114), Size = new Size(720, 62), ColumnCount = 3, BackColor = Color.White };
        for (var i = 0; i < 3; i++) steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        foreach (var (caption, i) in new[] { ("STEP 1\nConnect by USB", 0), ("STEP 2\nEnter Wi-Fi credentials", 1), ("STEP 3\nDevice reboots and joins", 2) })
            steps.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, Padding = new Padding(14, 12, 8, 0), Font = new Font("Segoe UI", 9, i == 1 ? FontStyle.Bold : FontStyle.Regular), ForeColor = i == 1 ? Color.FromArgb(21, 101, 192) : Color.FromArgb(79, 91, 102), BackColor = i == 1 ? Color.FromArgb(238, 246, 253) : Color.White }, i, 0);
        steps.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(220, 224, 228)); for (var i = 0; i < 3; i++) e.Graphics.DrawRectangle(pen, i * steps.Width / 3, 0, steps.Width / 3, steps.Height - 1); };
        var card = new Panel { Location = new Point(30, 196), Size = new Size(720, 304), BackColor = Color.White, Padding = new Padding(20) };
        card.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(220, 223, 227)), 0, 0, card.Width - 1, card.Height - 1);
        var form = FormGrid(new Point(20, 18), 680);
        usbPort.Width = 280;
        refreshUsbPorts.Text = "Rescan";
        getUsbConfig.Text = "Identify";
        AddField(form, "USB port", usbPort, refreshUsbPorts, getUsbConfig);
        AddField(form, "Device identified", usbDeviceIdentity);
        AddField(form, "Wi-Fi network (SSID)", usbSsid);
        AddField(form, "Wi-Fi password", usbPassword);
        AddField(form, "Device label", usbAlias);
        applyUsbWifi.Text = "Send to device and reboot"; StylePrimary(applyUsbWifi);
        form.Controls.Add(applyUsbWifi, 1, form.RowCount); form.SetColumnSpan(applyUsbWifi, 2); form.RowCount++;
        form.Controls.Add(usbStatus, 1, form.RowCount); form.SetColumnSpan(usbStatus, 2);
        card.Controls.Add(form);
        page.Controls.AddRange([steps, card]);
        return page;
    }

    private TabPage BuildGroupPage()
    {
        var page = Page("Group", "Group and device IDs", "A group is an intercom channel, not a radio network. A group holds up to 16 devices on the same trusted LAN.");
        var summary = new Panel { Location = new Point(30, 112), Size = new Size(760, 92), BackColor = Color.White, Padding = new Padding(20, 16, 20, 12) };
        summary.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(220, 223, 227)), 0, 0, summary.Width - 1, summary.Height - 1);
        summary.Controls.Add(new Label { Text = "Current group", AutoSize = true, Location = new Point(20, 18) });
        summary.Controls.Add(new Label { Text = "This companion's ID", AutoSize = true, Location = new Point(20, 52) });
        var current = new Label { AutoSize = true, Location = new Point(190, 18), Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Color.FromArgb(61, 65, 71) };
        current.Name = "CurrentGroup";
        summary.Controls.Add(current);
        summary.Controls.Add(new Label { Text = $"{settings.NodeId:x8} · generated once, unique on this PC", AutoSize = true, Location = new Point(190, 52), ForeColor = Color.FromArgb(99, 103, 109) });
        var danger = new Panel { Location = new Point(30, 222), Size = new Size(760, 370), BackColor = Color.FromArgb(253, 243, 243), Padding = new Padding(18, 14, 18, 12) };
        danger.Paint += (_, e) => { using var border = new Pen(Color.FromArgb(178, 34, 34)); e.Graphics.DrawRectangle(border, 0, 0, danger.Width - 1, danger.Height - 1); e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(178, 34, 34)), 0, 0, 4, danger.Height); };
        danger.Controls.Add(new Label { Text = "Change the group ID", AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.FromArgb(142, 26, 26), Location = new Point(18, 14) });
        danger.Controls.Add(new Label { Text = "This breaks the intercom until every node carries the new ID.", AutoSize = true, ForeColor = Color.FromArgb(122, 32, 32), Location = new Point(18, 42) });
        danger.Controls.Add(new Label { Text = "· Devices on the old group disappear from this companion and can no longer hear it.\n· Each device reboots after its configuration update.\n· A device you cannot reach keeps the old group until you get to it physically.\n· A half-finished change leaves two isolated intercoms.", AutoSize = true, ForeColor = Color.FromArgb(61, 65, 71), Location = new Point(18, 70) });
        var fields = FormGrid(new Point(18, 155), 704);
        AddField(fields, "New group ID", groupId, new Label { Text = "exactly 4 characters, A–Z and 0–9", AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109), Padding = new Padding(0, 6, 0, 0) });
        AddField(fields, $"Type {settings.MeshId} to confirm", groupConfirmation);
        fields.Controls.Add(new Label { Text = "Apply to", AutoSize = true, Anchor = AnchorStyles.Left }, 0, fields.RowCount);
        fields.Controls.Add(applyCompanionGroup, 1, fields.RowCount); fields.RowCount++;
        fields.Controls.Add(applyActiveGroup, 1, fields.RowCount); fields.RowCount++;
        applyGroup.Text = "Change group ID"; StyleDanger(applyGroup);
        fields.Controls.Add(applyGroup, 1, fields.RowCount);
        danger.Controls.Add(fields);
        var ids = new Panel { Location = new Point(30, 608), Size = new Size(760, 86), BackColor = Color.White, Padding = new Padding(20, 14, 20, 12) };
        ids.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(220, 223, 227)), 0, 0, ids.Width - 1, ids.Height - 1);
        ids.Controls.Add(new Label { Text = "Device IDs", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(20, 14) });
        ids.Controls.Add(new Label { Text = "A device ID is unique inside the group. Change it from that device's Configure dialog; the companion refuses an ID already held by another visible device.", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Color.FromArgb(99, 103, 109), Location = new Point(20, 36) });
        page.Controls.AddRange([summary, danger, ids]);
        groupId.TextChanged += (_, _) => UpdateGroupButton();
        groupConfirmation.TextChanged += (_, _) => UpdateGroupButton();
        applyGroup.Click += async (_, _) => await ApplyGroupChangeAsync();
        return page;
    }

    private TabPage BuildFirmwarePage()
    {
        var page = Page("Firmware", "Firmware", "Signed packages only. Updates are queued one device at a time and complete only after the device re-announces the offered version.");
        var package = new Panel { Location = new Point(30, 112), Size = new Size(820, 82), BackColor = Color.White, Padding = new Padding(20, 14, 20, 10) };
        package.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(220, 224, 228)), 0, 0, package.Width - 1, package.Height - 1);
        package.Controls.Add(new Label { Text = "Signed firmware package", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, 14) });
        otaManifest.BorderStyle = BorderStyle.None; otaManifest.BackColor = Color.White; otaManifest.Location = new Point(20, 45); otaManifest.Width = 560;
        browseOtaManifest.Text = "Choose another package…"; browseOtaManifest.Location = new Point(600, 23); StyleSecondary(browseOtaManifest);
        package.Controls.AddRange([otaManifest, browseOtaManifest]);
        var queuePanel = new Panel { Location = new Point(30, 210), Size = new Size(820, 420), BackColor = Color.White, AutoScroll = true, Padding = new Padding(20, 14, 20, 14) };
        queuePanel.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(220, 224, 228)), 0, 0, queuePanel.Width - 1, queuePanel.Height - 1);
        queuePanel.Controls.Add(otaQueue);
        var queueTitle = new Label { Text = "Update queue", AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), Location = new Point(20, 15) };
        var queueDetail = new Label { Text = "sequential, one device at a time", AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109), Location = new Point(120, 18) };
        var addAll = PlainButton("Add all compatible"); addAll.Location = new Point(536, 10); addAll.Click += (_, _) => { foreach (var peer in displayPeers.Where(peer => peer.SupportsOta)) queuedOtaDevices.Add(peer.NodeId); RefreshOtaQueue(); };
        var start = new Button { Text = "Start queue…", AutoSize = true, Location = new Point(676, 10) }; StylePrimary(start); start.Click += async (_, _) => await StartQueuedOtaAsync();
        queuePanel.Controls.AddRange([queueTitle, queueDetail, addAll, start]);
        otaQueue.Location = new Point(20, 54); otaQueue.Width = 778;
        otaStatus.Location = new Point(30, 646); otaStatus.MaximumSize = new Size(820, 0);
        page.Controls.AddRange([package, queuePanel, otaStatus]);
        return page;
    }

    private TabPage BuildIdentityPage()
    {
        var page = Page("Identity", "Identity, audio and shortcuts", "Changes to audio devices are applied independently from discovery and the device grid.");
        var card = new Panel { Location = new Point(30, 112), Size = new Size(720, 430), BackColor = Color.White, Padding = new Padding(20) };
        card.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(220, 223, 227)), 0, 0, card.Width - 1, card.Height - 1);
        var panel = FormGrid(new Point(20, 18), 680);
        companionAlias.Width = 330; recordingDevice.Width = playbackDevice.Width = 390;
        AddField(panel, "Companion alias", companionAlias);
        AddField(panel, "Companion ID", new Label { Text = $"{settings.NodeId:x8} · fixed for this PC", AutoSize = true, Padding = new Padding(0, 6, 0, 0), ForeColor = Color.FromArgb(79, 91, 102) });
        AddField(panel, "Microphone", recordingDevice);
        AddField(panel, "Speaker", playbackDevice);
        var broadcastKey = new TextBox { Text = "Ctrl + Alt + B", Width = 160, ReadOnly = true, BackColor = Color.White };
        var replyKey = new TextBox { Text = "Ctrl + Alt + R", Width = 160, ReadOnly = true, BackColor = Color.White };
        AddField(panel, "Broadcast hotkey", broadcastKey);
        AddField(panel, "Reply hotkey", replyKey);
        var runInTray = new CheckBox { Text = "Run in notification area when closed", Checked = settings.RunInNotificationArea, AutoSize = true };
        var startWithWindows = new CheckBox { Text = "Start with Windows", Checked = settings.StartWithWindows, AutoSize = true };
        panel.Controls.Add(runInTray, 1, panel.RowCount); panel.RowCount++;
        panel.Controls.Add(startWithWindows, 1, panel.RowCount); panel.RowCount++;
        var save = new Button { Text = "Save", AutoSize = true }; StylePrimary(save);
        save.Click += (_, _) => SaveIdentityAudioSettings(runInTray.Checked, startWithWindows.Checked);
        panel.Controls.Add(save, 1, panel.RowCount); panel.RowCount++;
        panel.Controls.Add(new Label { Text = "Hotkeys work while the companion is running. Hold the hotkey to talk; release it to stop.", AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109) }, 1, panel.RowCount);
        card.Controls.Add(panel);
        var back = new Button { Text = "Back to Talk", AutoSize = true, Location = new Point(146, 558) }; StyleSecondary(back); back.Click += (_, _) => ShowTalk();
        page.Controls.AddRange([card, back]);
        return page;
    }

    private TabPage BuildDiagnosticsPage()
    {
        var page = Page("Diagnostics", "Diagnostics", "Live receive counters and the discovery/session log. Audio availability never controls discovery.");
        var cards = new TableLayoutPanel { Location = new Point(30, 112), Size = new Size(820, 80), ColumnCount = 4 };
        for (var i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        foreach (var (title, i) in new[] { ("UDP audio", 0), ("Decoded frames", 1), ("PLC frames", 2), ("Output buffer", 3) })
        {
            var card = new Panel { Dock = DockStyle.Fill, Margin = new Padding(i == 0 ? 0 : 4, 0, i == 3 ? 0 : 4, 0), BackColor = Color.White };
            card.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(220, 224, 228)), 0, 0, card.Width - 1, card.Height - 1);
            card.Controls.Add(new Label { Text = title, AutoSize = true, Location = new Point(12, 12), ForeColor = Color.FromArgb(99, 103, 109) });
            card.Controls.Add(new Label { Text = "—", AutoSize = true, Name = $"Diagnostic{i}", Font = new Font("Segoe UI", 15, FontStyle.Bold), Location = new Point(12, 34) });
            cards.Controls.Add(card, i, 0);
        }
        diagnosticsCounters.Visible = false;
        var copy = PlainButton("Copy log");
        copy.Location = new Point(30, 526);
        copy.Click += (_, _) => { try { Clipboard.SetText(ReadDiagnosticLog()); } catch { } };
        var save = PlainButton("Save log to file…");
        save.Location = new Point(122, 526);
        save.Click += (_, _) => SaveDiagnosticLog();
        var log = new TextBox { Location = new Point(30, 210), Size = new Size(820, 300), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(28, 31, 35), ForeColor = Color.FromArgb(222, 232, 238), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Cascadia Mono", 8), Text = ReadDiagnosticLog() };
        page.Controls.AddRange([cards, diagnosticsCounters, copy, save, log]);
        return page;
    }

    private void ShowSettings(string page)
    {
        talkPage.Visible = false;
        settingsPage.Visible = true;
        activeSettingsPage = page;
        var tabs = settingsPage.Controls.Find("SettingsTabs", true).OfType<TabControl>().FirstOrDefault();
        if (tabs is not null) tabs.SelectedIndex = page switch { "USB" => 0, "Group" => 1, "Firmware" => 2, "Identity" => 3, _ => 4 };
        UpdateSettingsNavigation();
        if (page == "Firmware") RefreshOtaQueue();
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
        var title = new Panel { Height = 32, Width = Math.Max(760, offlineDevices.Parent?.ClientSize.Width - 36 ?? 760), BackColor = Color.White };
        title.Controls.Add(new Label { Text = "Known, not responding", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(0, 7) });
        title.Controls.Add(new Label { Text = offline.Length == 0 ? "No devices kept from earlier sessions" : $"{offline.Length} device(s) kept from earlier sessions — they return when they announce", AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109), Location = new Point(170, 8) });
        if (offline.Length > 0) { var all = PlainButton($"Remove all {offline.Length}…"); all.Anchor = AnchorStyles.Top | AnchorStyles.Right; all.Location = new Point(title.Width - 118, 2); all.Click += (_, _) => RemoveAllOffline(activeIds, offline.Length); title.Controls.Add(all); }
        offlineDevices.Controls.Add(title);
        foreach (var device in offline)
        {
            var row = new Panel { Height = 30, Width = title.Width, BackColor = Color.White };
            row.Controls.Add(new Label { Text = "■", ForeColor = Color.FromArgb(201, 204, 208), AutoSize = true, Location = new Point(0, 8) });
            row.Controls.Add(new Label { Text = device.Alias, AutoEllipsis = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Location = new Point(18, 7), Size = new Size(150, 16) });
            row.Controls.Add(new Label { Text = $"{device.NodeId:x8} · {device.LastAddress}", AutoEllipsis = true, ForeColor = Color.FromArgb(99, 103, 109), Location = new Point(180, 7), Size = new Size(250, 16) });
            row.Controls.Add(new Label { Text = $"last seen {(DateTimeOffset.Now - device.LastSeen):g} ago", AutoSize = true, ForeColor = Color.FromArgb(131, 135, 141), Location = new Point(444, 7) });
            var remove = PlainButton("Remove…"); remove.Anchor = AnchorStyles.Top | AnchorStyles.Right; remove.Location = new Point(row.Width - 84, 1); remove.Click += (_, _) => RemoveKnownDevice(device); row.Controls.Add(remove); offlineDevices.Controls.Add(row);
        }
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
            var speaker = receiveSession.LastTalker?.Alias ?? "A device";
            nowKicker.Text = "RECEIVING";
            statusLabel.Text = $"{speaker} is speaking";
            statusLabel.ForeColor = Color.FromArgb(21, 101, 192);
            nowDetail.Text = $"UDP {statistics.AudioPackets:n0} · decoded {statistics.DecodedFrames:n0} · PLC {statistics.ConcealedFrames:n0} · output {audio.BufferedMilliseconds} ms";
        }
        diagnosticsCounters.Text = nowDetail.Text;
        if (receiveSession is not null)
        {
            var statistics = receiveSession.Statistics;
            foreach (var (value, name) in new[] { ($"{statistics.AudioPackets:n0}", "Diagnostic0"), ($"{statistics.DecodedFrames:n0}", "Diagnostic1"), ($"{statistics.ConcealedFrames:n0}", "Diagnostic2"), ($"{audio?.BufferedMilliseconds ?? 0} ms", "Diagnostic3") })
                foreach (var label in settingsPage.Controls.Find(name, true).OfType<Label>()) label.Text = value;
        }
        nowTitle.Text = statusLabel.Text;
        nowTitle.ForeColor = statusLabel.ForeColor;
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
        identityLabel.Text = $"●  {settings.Alias.ToUpperInvariant()}\n    ID {settings.NodeId:x8} · group {settings.MeshId} · {displayPeers.Count} of 16 devices";
        identityMicrophone.Text = $"MIC\n{TrimDeviceName((recordingDevice.SelectedItem as RecordingDevice)?.Name ?? "Not selected")}";
        identitySpeaker.Text = $"SPEAKER\n{TrimDeviceName((playbackDevice.SelectedItem as PlaybackDevice)?.Name ?? "Not selected")}";
    }

    private static string TrimDeviceName(string value) => value.Length <= 23 ? value : $"{value[..20]}…";

    private Panel SettingsNavItem(string title, string page)
    {
        var item = new Panel { Width = 238, Height = 48, Margin = Padding.Empty, Cursor = Cursors.Hand, BackColor = Color.Transparent, Tag = page };
        var caption = new Label { Text = title, Dock = DockStyle.Fill, Padding = new Padding(18, 15, 8, 0), Font = new Font("Segoe UI", 9), Cursor = Cursors.Hand };
        EventHandler select = (_, _) => ShowSettings(page);
        item.Click += select; caption.Click += select;
        item.Controls.Add(caption);
        item.Paint += (_, e) =>
        {
            if (page != activeSettingsPage) return;
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(21, 101, 192)), 0, 0, 3, item.Height);
            e.Graphics.DrawLine(new Pen(Color.FromArgb(222, 225, 229)), 3, item.Height - 1, item.Width, item.Height - 1);
        };
        return item;
    }

    private void UpdateSettingsNavigation()
    {
        foreach (var (page, item) in settingsNavItems)
        {
            var selected = page == activeSettingsPage;
            item.BackColor = selected ? Color.White : Color.Transparent;
            if (item.Controls.OfType<Label>().FirstOrDefault() is { } caption)
                caption.Font = new Font("Segoe UI", 9, selected ? FontStyle.Bold : FontStyle.Regular);
            item.Invalidate();
        }
    }

    private static TableLayoutPanel FormGrid(Point location, int width)
    {
        var grid = new TableLayoutPanel { Location = location, Width = width, AutoSize = true, ColumnCount = 3, GrowStyle = TableLayoutPanelGrowStyle.AddRows };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddField(TableLayoutPanel grid, string label, params Control[] fields)
    {
        var row = grid.RowCount++;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 10, 7) }, 0, row);
        var fieldPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        foreach (var field in fields) { field.Margin = new Padding(0, 2, 8, 2); fieldPanel.Controls.Add(field); }
        grid.Controls.Add(fieldPanel, 1, row);
        grid.SetColumnSpan(fieldPanel, 2);
    }

    private static Button Chip(string text)
    {
        var button = PlainButton(text);
        button.AutoSize = false; button.Height = 28; button.Width = text.Length > 7 ? 72 : 60;
        button.Margin = new Padding(0, 1, 5, 0);
        button.Padding = Padding.Empty;
        button.FlatAppearance.BorderColor = Color.FromArgb(205, 210, 215);
        return button;
    }

    private static void StylePrimary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
        button.BackColor = Color.FromArgb(21, 101, 192); button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Color.FromArgb(21, 101, 192); button.Padding = new Padding(12, 7, 12, 7);
    }

    private static void StyleSecondary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
        button.BackColor = Color.White; button.ForeColor = Color.FromArgb(23, 25, 28);
        button.FlatAppearance.BorderColor = Color.FromArgb(175, 181, 187); button.Padding = new Padding(8, 5, 8, 5);
    }

    private static void StyleDanger(Button button)
    {
        button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
        button.BackColor = Color.FromArgb(178, 34, 34); button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Color.FromArgb(178, 34, 34); button.Padding = new Padding(10, 6, 10, 6);
    }

    private void SaveIdentityAudioSettings(bool tray, bool startWithWindows)
    {
        settings.RunInNotificationArea = tray;
        settings.StartWithWindows = startWithWindows;
        SaveCompanionAlias();
        ApplyAudioDeviceSelection();
        settings.Save();
        nowDetail.Text = "Identity, audio and shortcut settings saved.";
    }

    private void RefreshOtaQueue()
    {
        otaQueue.Controls.Clear();
        var peers = displayPeers.Where(peer => queuedOtaDevices.Contains(peer.NodeId)).OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
        if (peers.Length == 0)
        {
            otaQueue.Controls.Add(new Label { Text = "No devices queued. Add compatible active devices to update them sequentially.", AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109), Padding = new Padding(0, 12, 0, 0) });
            return;
        }
        foreach (var peer in peers)
        {
            var row = new Panel { Width = 770, Height = 82, BackColor = Color.White, Margin = new Padding(0, 0, 0, 0) };
            row.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Color.FromArgb(242, 244, 245)), 0, row.Height - 1, row.Width, row.Height - 1);
            row.Controls.Add(new Label { Text = peer.Alias, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(12, 10) });
            row.Controls.Add(new Label { Text = $"{peer.FirmwareVersion}  →  selected package", AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109), Location = new Point(168, 11) });
            row.Controls.Add(new Label { Text = "Queued", AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Location = new Point(388, 11) });
            var progress = new Panel { Location = new Point(12, 42), Size = new Size(650, 6), BackColor = Color.FromArgb(233, 235, 238) };
            row.Controls.Add(progress);
            row.Controls.Add(new Label { Text = "Waiting for earlier devices", AutoSize = true, ForeColor = Color.FromArgb(131, 135, 141), Font = new Font("Segoe UI", 7.5f), Location = new Point(12, 57) });
            var remove = PlainButton("Remove"); remove.Location = new Point(686, 8); remove.Click += (_, _) => { queuedOtaDevices.Remove(peer.NodeId); RefreshOtaQueue(); };
            row.Controls.Add(remove); otaQueue.Controls.Add(row);
        }
    }

    private async Task StartQueuedOtaAsync()
    {
        var targets = displayPeers.Where(peer => queuedOtaDevices.Contains(peer.NodeId) && peer.SupportsOta).ToArray();
        if (targets.Length == 0) { otaStatus.Text = "Add at least one active, protocol p2-compatible device to the queue."; return; }
        otaTargetsOverride = targets;
        try { await StartOtaUpdateAsync(false); }
        finally { otaTargetsOverride = null; RefreshOtaQueue(); }
    }

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
