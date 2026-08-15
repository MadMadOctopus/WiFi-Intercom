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
    private readonly TableLayoutPanel deviceGrid = new() { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 3, BackColor = UiStyles.Surface, Padding = Padding.Empty };
    private readonly FlowLayoutPanel offlineDevices = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiStyles.White, Padding = Padding.Empty };
    private readonly Panel offlineFrame = new() { Dock = DockStyle.Top, AutoSize = true, BackColor = UiStyles.White, Padding = Padding.Empty, Visible = false };
    private readonly TextBox deviceFilter = new() { Width = 150, PlaceholderText = "Alias or ID" };
    private readonly Label deviceCount = new() { AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary };
    private readonly Label nowKicker = new() { AutoSize = true, ForeColor = UiStyles.Secondary, Font = UiStyles.Hint };
    private readonly Label nowTitle = new() { AutoSize = true, Font = UiStyles.NowTitle };
    private readonly Label nowDetail = new() { AutoSize = false, ForeColor = UiStyles.Secondary, Font = UiStyles.SecondaryFont, TextAlign = ContentAlignment.MiddleRight };
    private readonly FlowLayoutPanel activity = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiStyles.White, Padding = Padding.Empty };
    private readonly Panel pageHost = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel talkPage = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiStyles.Surface };
    private readonly TableLayoutPanel settingsPage = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiStyles.Surface, Visible = false };
    private readonly System.Windows.Forms.Timer cardPulse = new() { Interval = 60 };
    private readonly TextBox groupId = new() { MaxLength = 4, CharacterCasing = CharacterCasing.Upper, Width = 90 };
    private readonly TextBox groupConfirmation = new() { MaxLength = 4, CharacterCasing = CharacterCasing.Upper, Width = 90 };
    private readonly CheckBox applyCompanionGroup = new() { Text = "This companion", Checked = true, AutoSize = true };
    private readonly CheckBox applyActiveGroup = new() { Text = "All active devices, sequentially", AutoSize = true };
    private readonly DisabledTintButton applyGroup = new() { Text = "Change group ID", BackColor = UiStyles.Red, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Enabled = false, AutoSize = true, DisabledTint = Color.FromArgb(208, 138, 138) };
    private readonly Label diagnosticsCounters = new() { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private readonly CheckBox localMute = new() { Text = "Mute my speaker", AutoSize = true };
    private readonly Label identityMicrophone = new() { AutoEllipsis = true };
    private readonly Label identitySpeaker = new() { AutoEllipsis = true };
    private readonly Label usbDeviceIdentity = new() { AutoSize = true, ForeColor = Color.FromArgb(79, 91, 102) };
    private readonly Panel settingsNavigation = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, Label> settingsNavItems = [];
    private readonly Panel settingsContentHost = new() { Dock = DockStyle.Fill, BackColor = UiStyles.Surface };
    private readonly Dictionary<string, UserControl> settingsViews = [];
    private readonly Dictionary<string, Panel> filterChips = [];
    private readonly FlowLayoutPanel otaQueue = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Label otaPackageSummary = new() { AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary };
    private readonly Label otaPackageName = new() { AutoSize = true, Font = UiStyles.PanelHeading, ForeColor = UiStyles.Ink };
    private readonly HashSet<uint> queuedOtaDevices = [];
    private string activeSettingsPage = "USB";
    private IReadOnlyList<Peer> displayPeers = [];
    private string activeFilter = "All";
    private float pulsePhase;
    private NotifyIcon? trayIcon;
    private bool allowExit;
    private readonly TableLayoutPanel shell = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = UiStyles.Surface };
    private readonly TableLayoutPanel identityStrip = new() { Dock = DockStyle.Fill, BackColor = UiStyles.White, Padding = new Padding(20, 4, 20, 4), ColumnCount = 9 };
    private readonly Panel identityDot = new() { Size = new Size(9, 9), Anchor = AnchorStyles.Left };
    private readonly Panel nowBar = new() { Dock = DockStyle.Fill };
    private readonly Panel nowAccent = new() { Size = new Size(4, 44), Anchor = AnchorStyles.Left };
    private readonly Panel statusBar = new() { Dock = DockStyle.Fill, BackColor = UiStyles.Chrome, Padding = new Padding(16, 0, 16, 0) };
    private readonly Label statusRight = new() { AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Right };

    private void BuildRedesign()
    {
        // Keep the controls which carry the real settings/event bindings out
        // of the temporary legacy tree. ControlCollection.Clear disposes child
        // controls, which otherwise leaves the settings tabs with blank or
        // clipped fields on a fresh launch.
        DetachLegacyControls();
        Controls.Clear();
        Text = "Wi-Fi Intercom Companion";
        MinimumSize = new Size(1024, 700);
        Size = new Size(1280, 820);
        BackColor = UiStyles.Surface;
        AutoScaleMode = AutoScaleMode.Dpi;
        AcceptButton = null;

        shell.Controls.Clear();
        shell.RowStyles.Clear();
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        // The original (pre-redesign) controls were children of the legacy
        // surface.  Create redesign-owned controls here rather than moving
        // children out of a surface which Controls.Clear() has disposed.
        identityLabel = new Label { AutoSize = false };

        identityStrip.Controls.Clear();
        identityStrip.ColumnStyles.Clear();
        identityStrip.RowStyles.Clear();
        identityStrip.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 19));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        identityStrip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        identityDot.BackColor = UiStyles.Green;
        var identityBlock = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        identityBlock.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        identityBlock.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        identityBlock.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        identityLabel.AutoSize = false;
        identityLabel.Dock = DockStyle.Fill;
        identityLabel.Font = UiStyles.SecondaryFont;
        identityLabel.TextAlign = ContentAlignment.BottomLeft;
        var identityMeta = new Label { Name = "IdentityMeta", Dock = DockStyle.Fill, ForeColor = UiStyles.Secondary, Font = UiStyles.Hint, TextAlign = ContentAlignment.TopLeft };
        identityBlock.Controls.Add(identityLabel, 0, 0);
        identityBlock.Controls.Add(identityMeta, 0, 1);
        foreach (var label in new[] { identityMicrophone, identitySpeaker })
        {
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.Font = UiStyles.SecondaryFont;
            label.ForeColor = UiStyles.Body;
            label.TextAlign = ContentAlignment.MiddleLeft;
        }
        localMute.Appearance = Appearance.Button;
        localMute.AutoSize = false;
        localMute.Width = 130;
        localMute.Height = 29;
        localMute.TextAlign = ContentAlignment.MiddleCenter;
        localMute.Font = UiStyles.SecondaryFont;
        localMute.FlatStyle = FlatStyle.Flat;
        localMute.BackColor = UiStyles.White;
        localMute.FlatAppearance.BorderColor = UiStyles.ControlBorder;
        localMute.FlatAppearance.CheckedBackColor = Color.FromArgb(242, 244, 245);
        localMute.CheckedChanged += (_, _) => audio?.SetLocalPlaybackMuted(localMute.Checked);
        var changeAudio = new LinkLabel { Text = "Change…", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(22, 0, 0, 0), Font = UiStyles.SecondaryFont, LinkColor = UiStyles.Blue, ActiveLinkColor = UiStyles.DeepBlue };
        changeAudio.Click += (_, _) => ShowSettings("Identity");
        var settingsButton = PlainButton("Settings…");
        settingsButton.Margin = new Padding(8, 0, 0, 0);
        settingsButton.Click += (_, _) => ShowSettings("USB");
        identityStrip.Controls.Add(identityDot, 0, 0);
        identityStrip.Controls.Add(identityBlock, 1, 0);
        identityStrip.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(230, 232, 234), Margin = new Padding(0, 10, 0, 10) }, 2, 0);
        identityStrip.Controls.Add(identityMicrophone, 3, 0);
        identityStrip.Controls.Add(identitySpeaker, 4, 0);
        identityStrip.Controls.Add(changeAudio, 5, 0);
        identityStrip.Controls.Add(localMute, 7, 0);
        identityStrip.Controls.Add(settingsButton, 8, 0);
        identityStrip.Paint += (_, eventArgs) => UiStyles.DrawBorder(eventArgs, identityStrip, UiStyles.Border);

        nowBar.Controls.Clear();
        nowBar.BackColor = UiStyles.Surface;
        nowAccent.BackColor = UiStyles.Green;
        nowAccent.Location = new Point(20, 16);
        statusLabel.Visible = false;
        nowTitle.Location = new Point(40, 26);
        nowTitle.Text = "Idle";
        nowTitle.ForeColor = UiStyles.Green;
        nowKicker.Location = new Point(40, 11);
        nowKicker.Text = "IDLE";
        nowDetail.Size = new Size(370, 44);
        nowDetail.Location = new Point(Math.Max(440, nowBar.Width - 390), 16);
        nowDetail.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        nowDetail.Visible = true;
        nowBar.Controls.AddRange([nowAccent, nowKicker, nowTitle, nowDetail]);
        nowBar.Paint += (_, eventArgs) => UiStyles.DrawBorder(eventArgs, nowBar, UiStyles.Border);

        statusBar.Controls.Clear();
        networkLabel.AutoSize = true;
        networkLabel.Font = UiStyles.Hint;
        networkLabel.ForeColor = UiStyles.Secondary;
        networkLabel.Anchor = AnchorStyles.Left;
        statusRight.Text = "Running in the notification area · Ctrl+Alt+B to broadcast";
        var statusLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusLayout.Controls.Add(networkLabel, 0, 0);
        statusLayout.Controls.Add(statusRight, 1, 0);
        statusBar.Controls.Add(statusLayout);
        statusBar.Paint += (_, eventArgs) => UiStyles.DrawBorder(eventArgs, statusBar, UiStyles.Border);

        BuildTalkPage();
        BuildSettingsPage();
        pageHost.Controls.Add(settingsPage);
        pageHost.Controls.Add(talkPage);
        shell.Controls.Add(identityStrip, 0, 0);
        shell.Controls.Add(nowBar, 0, 1);
        shell.Controls.Add(pageHost, 0, 2);
        shell.Controls.Add(statusBar, 0, 3);
        Controls.Add(shell);

        deviceFilter.TextChanged += (_, _) => RefreshRedesignPeers();
        deviceGrid.Resize += (_, _) => LayoutDeviceCards();
        cardPulse.Tick += (_, _) => PulseCards();
        cardPulse.Start();
        UpdateIdentityPresentation();
        UpdateNowBarAppearance();
    }

    private void DetachLegacyControls()
    {
        foreach (var control in new Control[]
        {
            companionAlias, recordingDevice, playbackDevice, usbPort, usbSsid,
            usbPassword, usbAlias, refreshUsbPorts, applyUsbWifi, usbStatus,
            otaManifest, browseOtaManifest, otaStatus, networkLabel, statusLabel,
            getUsbConfig, applyAudioDevices, saveCompanionAlias
        })
        {
            control.Parent?.Controls.Remove(control);
        }
    }

    private void BuildTalkPage()
    {
        talkPage.Controls.Clear();
        talkPage.ColumnStyles.Clear();
        talkPage.RowStyles.Clear();
        talkPage.Padding = new Padding(20, 18, 20, 18);
        talkPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        talkPage.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 336));
        talkPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = UiStyles.Surface };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0, 0, 12, 0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Devices", AutoSize = true, Font = UiStyles.CardAlias, Anchor = AnchorStyles.Left }, 0, 0);
        deviceCount.Margin = new Padding(10, 0, 0, 0);
        deviceCount.Anchor = AnchorStyles.Left;
        header.Controls.Add(deviceCount, 1, 0);
        var filters = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        UiStyles.StyleInput(deviceFilter, 150);
        deviceFilter.Margin = new Padding(0, 0, 6, 0);
        filters.Controls.Add(deviceFilter);
        foreach (var name in new[] { "All", "Speaking", "Muted", "Silenced", "Legacy" }) filters.Controls.Add(CreateFilterChip(name));
        header.Controls.Add(filters, 3, 0);

        deviceGrid.Controls.Clear();
        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = UiStyles.Surface, Margin = new Padding(0, 0, 12, 0) };
        gridHost.Controls.Add(deviceGrid);
        offlineFrame.Controls.Clear();
        offlineFrame.Controls.Add(offlineDevices);
        offlineFrame.Paint += (_, e) => UiStyles.DrawBorder(e, offlineFrame);
        var note = new Label
        {
            Text = "Up to 16 devices per group. Cards are one row high and the grid scrolls. Silence is a soft mute on the device: it stops received audio while discovery and floor control continue. A device whose physical mute slider is on reports that state and its Silence control is unavailable.",
            AutoSize = true, MaximumSize = new Size(900, 0), Font = UiStyles.Hint, ForeColor = UiStyles.Muted, Margin = new Padding(0, 12, 12, 0), Padding = new Padding(0, 0, 0, 0)
        };
        left.Controls.Add(header, 0, 0);
        left.Controls.Add(gridHost, 0, 1);
        left.Controls.Add(offlineFrame, 0, 2);
        left.Controls.Add(note, 0, 3);

        var right = BuildTalkActionsPanel();
        talkPage.Controls.Add(left, 0, 0);
        talkPage.Controls.Add(right, 1, 0);
    }

    private Control BuildTalkActionsPanel()
    {
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = UiStyles.Surface, Padding = new Padding(12, 0, 0, 0) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var ptt = UiStyles.BorderedPanel(new Padding(16));
        ptt.AutoSize = false;
        ptt.Height = 230;
        ptt.Paint += (_, e) => UiStyles.DrawBorder(e, ptt);
        var commands = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        broadcast = CreatePttButton("Hold to broadcast", UiStyles.Green);
        replySurface = CreatePttButton("Hold to reply", UiStyles.Blue);
        broadcast.Enabled = replySurface.Enabled = receiveSession is not null;
        BindPtt(broadcast, () => receiveSession?.PressBroadcast());
        BindPtt(replySurface, () => receiveSession?.PressReply());
        broadcast.AutoSize = false;
        broadcast.Height = 68;
        broadcast.Dock = DockStyle.Top;
        broadcast.Font = UiStyles.Broadcast;
        broadcast.Padding = new Padding(0, 12, 0, 12);
        broadcast.BackColor = UiStyles.Green;
        broadcast.FlatAppearance.BorderColor = UiStyles.Green;
        replySurface.AutoSize = false;
        replySurface.Height = 48;
        replySurface.Dock = DockStyle.Top;
        replySurface.Font = UiStyles.CardAlias;
        replySurface.Padding = new Padding(0, 7, 0, 7);
        replySurface.BackColor = UiStyles.Blue;
        replySurface.FlatAppearance.BorderColor = UiStyles.Blue;
        commands.Controls.Add(broadcast, 0, 0);
        commands.Controls.Add(HintRow($"Everyone in group {settings.MeshId}", "Space, or Ctrl+Alt+B anywhere"), 0, 1);
        commands.Controls.Add(new Panel { Height = 12, Dock = DockStyle.Top, BackColor = UiStyles.White }, 0, 2);
        commands.Controls.Add(replySurface, 0, 3);
        commands.Controls.Add(HintRow("Last sender: none", "Ctrl+Alt+R"), 0, 4);
        ptt.Controls.Add(commands);

        var activityPanel = UiStyles.BorderedPanel(Padding.Empty);
        activityPanel.Paint += (_, e) => UiStyles.DrawBorder(e, activityPanel);
        var activityHeader = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(16, 0, 16, 0), ColumnCount = 2 };
        activityHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activityHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        activityHeader.Controls.Add(new Label { Text = "Activity", AutoSize = true, Font = UiStyles.PanelHeading, Anchor = AnchorStyles.Left }, 0, 0);
        var recordings = new LinkLabel { Text = "Open recordings folder", AutoSize = true, Font = UiStyles.SecondaryFont, LinkColor = UiStyles.Blue, Anchor = AnchorStyles.Right };
        recordings.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CompanionSettings.RecordingsDirectory) { UseShellExecute = true }); } catch { } };
        activityHeader.Controls.Add(recordings, 1, 0);
        var diagnostics = new LinkLabel { Text = "Diagnostics…", AutoSize = true, Dock = DockStyle.Bottom, Font = UiStyles.SecondaryFont, Padding = new Padding(16, 10, 0, 10), LinkColor = UiStyles.Blue };
        diagnostics.Click += (_, _) => ShowSettings("Diagnostics");
        activity.Controls.Clear();
        activityPanel.Controls.Add(activity);
        activityPanel.Controls.Add(diagnostics);
        activityPanel.Controls.Add(UiStyles.Rule());
        activityPanel.Controls.Add(activityHeader);
        right.Controls.Add(ptt, 0, 0);
        right.Controls.Add(new Panel { BackColor = UiStyles.Surface }, 0, 1);
        right.Controls.Add(activityPanel, 0, 2);
        return right;
    }

    private static TableLayoutPanel HintRow(string left, string right)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label { Text = left, AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left }, 0, 0);
        row.Controls.Add(new Label { Text = right, AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Right }, 1, 0);
        return row;
    }

    private void BuildSettingsPage()
    {
        settingsPage.Controls.Clear();
        settingsPage.ColumnStyles.Clear();
        settingsPage.RowStyles.Clear();
        settingsPage.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
        settingsPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        settingsNavigation.Controls.Clear();
        settingsNavItems.Clear();
        var footer = new Label
        {
            AutoSize = true, MaximumSize = new Size(200, 0), Padding = new Padding(18, 0, 18, 0), Margin = new Padding(0, 14, 0, 0), Dock = DockStyle.Top,
            Text = "Settings are rare. Nothing here blocks discovery, talking or the device list.",
            Font = UiStyles.Hint, ForeColor = UiStyles.Muted,
        };
        settingsNavigation.Controls.Add(footer);
        foreach (var (title, page) in new[] { ("Diagnostics", "Diagnostics"), ("Identity, audio, shortcuts", "Identity"), ("Firmware", "Firmware"), ("Group and device IDs", "Group"), ("Set up a device (USB)", "USB") })
        {
            var item = SettingsNavItem(title, page);
            settingsNavItems.Add(page, item);
            settingsNavigation.Controls.Add(item);
        }
        settingsNavigation.Controls.Add(new Label { Text = "SETTINGS", ForeColor = UiStyles.Muted, AutoSize = false, Height = 42, Dock = DockStyle.Top, Padding = new Padding(18, 0, 0, 10), Font = UiStyles.Hint, Margin = Padding.Empty, TextAlign = ContentAlignment.BottomLeft });
        var navHost = new Panel { Dock = DockStyle.Fill, BackColor = UiStyles.Chrome };
        navHost.Controls.Add(settingsNavigation);
        settingsContentHost.Controls.Clear();
        settingsViews.Clear();
        foreach (var view in new (string Name, UserControl View)[]
        {
            ("USB", BuildUsbPage()), ("Group", BuildGroupPage()),
            ("Firmware", BuildFirmwarePage()), ("Identity", BuildIdentityPage()),
            ("Diagnostics", BuildDiagnosticsPage())
        })
        {
            view.View.Dock = DockStyle.Fill;
            view.View.Visible = false;
            settingsViews.Add(view.Name, view.View);
            settingsContentHost.Controls.Add(view.View);
        }
        settingsPage.Controls.Add(navHost, 0, 0);
        settingsPage.Controls.Add(settingsContentHost, 1, 0);
    }

    private UserControl Page(string name, string title, string detail, int maxWidth, out FlowLayoutPanel content)
    {
        var page = new UserControl { Name = name, BackColor = UiStyles.Surface, Padding = Padding.Empty };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiStyles.Surface, Padding = new Padding(28, 24, SystemInformation.VerticalScrollBarWidth + 28, 24) };
        content = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = maxWidth, BackColor = UiStyles.Surface, Margin = Padding.Empty };
        content.Controls.Add(new Label { Text = title, AutoSize = true, Font = UiStyles.PageHeading, ForeColor = UiStyles.Ink, Margin = Padding.Empty });
        if (!string.IsNullOrEmpty(detail))
            content.Controls.Add(new Label { Text = detail, AutoSize = true, MaximumSize = new Size(maxWidth, 0), Font = UiStyles.BodyFont, ForeColor = UiStyles.Secondary, Margin = new Padding(0, 6, 0, 0) });
        scroll.Controls.Add(content);
        page.Controls.Add(scroll);
        return page;
    }

    private UserControl BuildUsbPage()
    {
        var page = Page("USB", "Set up a device over USB", "Connect the device by USB, give it your Wi-Fi credentials, and it reboots onto the network. The password stays on this PC and is never saved by the companion.", 720, out var content);
        var steps = new TableLayoutPanel { Width = 720, Height = 68, ColumnCount = 3, BackColor = UiStyles.White, Margin = new Padding(0, 22, 0, 0) };
        for (var i = 0; i < 3; i++) steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        var stepData = new[]
        {
            ("STEP 1", "Connect by USB", UiStyles.White, UiStyles.Green, UiStyles.Ink),
            ("STEP 2", "Enter Wi-Fi credentials", UiStyles.BlueTint, UiStyles.Blue, UiStyles.DeepBlue),
            ("STEP 3", "Device reboots and appears", UiStyles.Surface, UiStyles.Disabled, UiStyles.Muted),
        };
        for (var i = 0; i < stepData.Length; i++)
        {
            var step = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = stepData[i].Item3, Padding = new Padding(14, 10, 8, 8), ColumnCount = 1 };
            step.Controls.Add(new Label { Text = stepData[i].Item1, AutoSize = true, Font = UiStyles.Hint, ForeColor = stepData[i].Item4 }, 0, 0);
            step.Controls.Add(new Label { Text = stepData[i].Item2, AutoSize = true, Font = UiStyles.BodyFont, ForeColor = stepData[i].Item5, Margin = new Padding(0, 3, 0, 0) }, 0, 1);
            steps.Controls.Add(step, i, 0);
        }
        steps.Paint += (_, e) => UiStyles.DrawBorder(e, steps);
        var card = UiStyles.BorderedPanel(new Padding(20));
        card.AutoSize = true; card.AutoSizeMode = AutoSizeMode.GrowAndShrink; card.Width = 720; card.Margin = new Padding(0, 18, 0, 0);
        card.Paint += (_, e) => UiStyles.DrawBorder(e, card);
        var form = FormGrid(150);
        UiStyles.StyleInput(usbPort, 280);
        UiStyles.StyleInput(usbSsid, 240);
        UiStyles.StyleInput(usbPassword, 240);
        UiStyles.StyleInput(usbAlias, 240);
        refreshUsbPorts.Text = "Rescan"; UiStyles.StyleButton(refreshUsbPorts, new Padding(12, 6, 12, 6));
        usbDeviceIdentity.Text = string.IsNullOrWhiteSpace(usbDeviceIdentity.Text) ? "Connect a device to identify it over USB" : usbDeviceIdentity.Text;
        usbDeviceIdentity.ForeColor = UiStyles.Green; usbDeviceIdentity.Font = UiStyles.SecondaryFont;
        refreshUsbPorts.Click += async (_, _) => await ReadUsbConfigurationAsync();
        var portRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        portRow.Controls.AddRange([usbPort, Spacer(8), refreshUsbPorts, Spacer(8), usbDeviceIdentity]);
        AddField(form, "USB port", portRow);
        var wifiRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        wifiRow.Controls.AddRange([usbSsid, Spacer(8), new Label { Text = "2.4 GHz only", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Padding = new Padding(0, 6, 0, 0) }]);
        AddField(form, "Wi-Fi network", wifiRow);
        var show = PlainButton("Show");
        show.Click += (_, _) => { usbPassword.UseSystemPasswordChar = !usbPassword.UseSystemPasswordChar; show.Text = usbPassword.UseSystemPasswordChar ? "Show" : "Hide"; };
        var passwordRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        passwordRow.Controls.AddRange([usbPassword, Spacer(8), show]);
        AddField(form, "Password", passwordRow);
        AddField(form, "Device alias", usbAlias);
        applyUsbWifi.Text = "Send to device and reboot"; StylePrimary(applyUsbWifi);
        var submit = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BorderStyle = BorderStyle.None, Margin = new Padding(0, 20, 0, 0), Padding = new Padding(0, 16, 0, 0) };
        submit.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, 0, submit.Width, 0); };
        submit.Controls.Add(applyUsbWifi);
        submit.Controls.Add(new Label { Text = "The device restarts and should appear in Devices within a few seconds.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(12, 7, 0, 0) });
        form.Controls.Add(submit, 1, form.RowCount); form.RowCount++;
        card.Controls.Add(form);
        content.Controls.Add(steps);
        content.Controls.Add(card);
        return page;
    }

    private UserControl BuildGroupPage()
    {
        var page = Page("Group", "Group and device IDs", "A group is an intercom channel, not a radio network. A group holds up to 16 devices on the same trusted LAN.", 760, out var content);
        var summary = UiStyles.BorderedPanel(new Padding(20, 14, 20, 14)); summary.Width = 760; summary.AutoSize = true; summary.Margin = new Padding(0, 18, 0, 0); summary.Paint += (_, e) => UiStyles.DrawBorder(e, summary);
        var summaryFields = FormGrid(170);
        var current = new Label { AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Body, Name = "CurrentGroup", Padding = new Padding(0, 5, 0, 0) };
        AddField(summaryFields, "Current group", current);
        AddField(summaryFields, "This companion's ID", new Label { Text = $"{settings.NodeId:x8} · generated once, unique on this PC", AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Secondary, Padding = new Padding(0, 5, 0, 0) });
        summary.Controls.Add(summaryFields);

        var danger = new TableLayoutPanel { BackColor = UiStyles.RedTint, Width = 760, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0, 18, 0, 0) };
        danger.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        danger.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        danger.Paint += (_, e) => { UiStyles.DrawBorder(e, danger, UiStyles.Red); using var brush = new SolidBrush(UiStyles.Red); e.Graphics.FillRectangle(brush, 0, 0, 4, danger.Height); };
        var dangerHeader = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 756, Padding = new Padding(18, 14, 18, 14), Margin = Padding.Empty };
        dangerHeader.Controls.Add(new Label { Text = "Change the group ID", AutoSize = true, Font = UiStyles.PanelHeading, ForeColor = UiStyles.DarkRed, Margin = Padding.Empty });
        dangerHeader.Controls.Add(new Label { Text = "This breaks the intercom until every node carries the new ID.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = Color.FromArgb(122, 32, 32), Margin = new Padding(0, 4, 0, 0) });
        dangerHeader.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(242, 214, 214)); e.Graphics.DrawLine(pen, 0, dangerHeader.Height - 1, dangerHeader.Width, dangerHeader.Height - 1); };
        var dangerBody = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 756, Padding = new Padding(18, 16, 18, 16), Margin = Padding.Empty };
        var dangerStack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 720, Margin = Padding.Empty };
        dangerStack.Controls.Add(new Label { Text = $"· Devices still on {settings.MeshId} disappear from this companion and can no longer hear it.\n· Each device must be changed separately, over USB or with a configuration write, and each one reboots.\n· A device you cannot reach right now keeps the old group until you get to it physically.\n· Two groups on the same LAN never mix, so a half-finished change leaves two isolated intercoms.", AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Body, Margin = Padding.Empty });
        var fields = FormGrid(170);
        fields.Margin = new Padding(0, 18, 0, 0);
        UiStyles.StyleInput(groupId, 140); UiStyles.StyleInput(groupConfirmation, 140);
        AddField(fields, "New group ID", groupId, new Label { Text = "exactly 4 characters, A–Z and 0–9", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Padding = new Padding(10, 5, 0, 0) });
        AddField(fields, $"Type {settings.MeshId} to confirm", groupConfirmation);
        applyActiveGroup.Text = $"All {displayPeers.Count} active devices, one at a time (each reboots)";
        var choices = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        choices.Controls.Add(applyCompanionGroup); choices.Controls.Add(applyActiveGroup);
        AddField(fields, "Apply to", choices);
        applyGroup.Text = "Change group ID"; StyleDanger(applyGroup);
        var action = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        action.Controls.Add(applyGroup);
        action.Controls.Add(new Label { Text = "Enabled once the confirmation matches.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(12, 7, 0, 0) });
        fields.Controls.Add(action, 1, fields.RowCount); fields.RowCount++;
        dangerStack.Controls.Add(fields);
        dangerBody.Controls.Add(dangerStack);
        danger.Controls.Add(dangerHeader, 0, 0);
        danger.Controls.Add(dangerBody, 0, 1);

        var ids = UiStyles.BorderedPanel(new Padding(20, 14, 20, 14)); ids.Width = 760; ids.AutoSize = true; ids.Margin = new Padding(0, 16, 0, 0); ids.Paint += (_, e) => UiStyles.DrawBorder(e, ids);
        var idStack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 718 };
        idStack.Controls.Add(new Label { Text = "Device IDs", AutoSize = true, Font = UiStyles.PanelHeading });
        idStack.Controls.Add(new Label { Text = "A device ID is unique inside the group. Change it from that device's Configure dialog; the companion refuses an ID already held by another visible device.", AutoSize = true, MaximumSize = new Size(700, 0), Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(0, 5, 0, 0) });
        ids.Controls.Add(idStack);
        content.Controls.Add(summary); content.Controls.Add(danger); content.Controls.Add(ids);
        groupId.TextChanged += (_, _) => UpdateGroupButton();
        groupConfirmation.TextChanged += (_, _) => UpdateGroupButton();
        applyGroup.Click += async (_, _) => await ApplyGroupChangeAsync();
        return page;
    }

    private UserControl BuildFirmwarePage()
    {
        var page = Page("Firmware", "Firmware", "Signed packages only. Updates are queued one device at a time and complete only after the device re-announces the offered version.", 820, out var content);
        var package = UiStyles.BorderedPanel(new Padding(20, 18, 20, 18)); package.Width = 820; package.AutoSize = true; package.Margin = new Padding(0, 18, 0, 0); package.Paint += (_, e) => UiStyles.DrawBorder(e, package);
        var packageLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        packageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); packageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var packageInfo = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        otaPackageName.Text = "No package selected";
        packageInfo.Controls.Add(otaPackageName);
        otaPackageSummary.Text = "Choose a signed OTA package to verify it here.";
        packageInfo.Controls.Add(otaPackageSummary);
        browseOtaManifest.Text = "Choose another package…"; StyleSecondary(browseOtaManifest); browseOtaManifest.Anchor = AnchorStyles.Right;
        packageLayout.Controls.Add(packageInfo, 0, 0); packageLayout.Controls.Add(browseOtaManifest, 1, 0);
        package.Controls.Add(packageLayout);

        var queuePanel = UiStyles.BorderedPanel(Padding.Empty); queuePanel.Width = 820; queuePanel.AutoSize = true; queuePanel.Margin = new Padding(0, 16, 0, 0); queuePanel.Paint += (_, e) => UiStyles.DrawBorder(e, queuePanel);
        var queueLayout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Width = 818, Margin = Padding.Empty };
        queueLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        queueLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        queueLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var queueHeader = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 52, ColumnCount = 3, Padding = new Padding(20, 0, 20, 0) };
        queueHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); queueHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); queueHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var queueCaption = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        queueCaption.Controls.Add(new Label { Text = "Update queue", AutoSize = true, Font = new Font(UiStyles.BodyFont, FontStyle.Bold) });
        queueCaption.Controls.Add(new Label { Text = "sequential, one device at a time", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(10, 1, 0, 0) });
        var addAll = PlainButton("Add all compatible"); addAll.Click += (_, _) => { foreach (var peer in displayPeers.Where(peer => peer.SupportsOta)) queuedOtaDevices.Add(peer.NodeId); RefreshOtaQueue(); };
        var start = new Button { Text = "Start queue…" }; StylePrimary(start); start.Click += async (_, _) => await StartQueuedOtaAsync();
        queueHeader.Controls.Add(queueCaption, 0, 0); queueHeader.Controls.Add(addAll, 1, 0); queueHeader.Controls.Add(start, 2, 0);
        var queueHost = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 818, Padding = new Padding(20, 0, 20, 0), BackColor = UiStyles.White, Margin = Padding.Empty };
        otaQueue.Width = 778; otaQueue.Margin = Padding.Empty; queueHost.Controls.Add(otaQueue);
        var queueFooter = new Label { Text = "Keep the companion open. Windows may ask once to allow the temporary local firmware server on a private network.", AutoSize = true, MaximumSize = new Size(778, 0), Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Padding = new Padding(20, 14, 20, 14), Margin = Padding.Empty };
        queueLayout.Controls.Add(queueHeader, 0, 0);
        queueLayout.Controls.Add(queueHost, 0, 1);
        queueLayout.Controls.Add(queueFooter, 0, 2);
        queueLayout.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, 51, queueLayout.Width, 51); };
        queuePanel.Controls.Add(queueLayout);
        otaStatus.MaximumSize = new Size(820, 0); otaStatus.Font = UiStyles.SecondaryFont; otaStatus.Margin = new Padding(0, 12, 0, 0);
        content.Controls.Add(package); content.Controls.Add(queuePanel); content.Controls.Add(otaStatus);
        return page;
    }

    private UserControl BuildIdentityPage()
    {
        var page = Page("Identity", "Identity, audio and shortcuts", "Choose the local name, audio devices and global push-to-talk shortcuts for this companion.", 720, out var content);
        var card = UiStyles.BorderedPanel(new Padding(20)); card.Width = 720; card.AutoSize = true; card.Margin = new Padding(0, 18, 0, 0); card.Paint += (_, e) => UiStyles.DrawBorder(e, card);
        var panel = FormGrid(170);
        UiStyles.StyleInput(companionAlias, 260); UiStyles.StyleInput(recordingDevice, 320); UiStyles.StyleInput(playbackDevice, 320);
        AddField(panel, "Companion alias", companionAlias);
        AddField(panel, "Companion ID", new Label { Text = $"{settings.NodeId:x8} · fixed for this PC", AutoSize = true, Font = UiStyles.BodyFont, Padding = new Padding(0, 5, 0, 0), ForeColor = UiStyles.Secondary });
        AddField(panel, "Microphone", recordingDevice);
        AddField(panel, "Speaker", playbackDevice);
        var broadcastKey = new TextBox { Text = "Ctrl + Alt + B", ReadOnly = true, BackColor = UiStyles.White };
        var replyKey = new TextBox { Text = "Ctrl + Alt + R", ReadOnly = true, BackColor = UiStyles.White };
        UiStyles.StyleInput(broadcastKey, 130); UiStyles.StyleInput(replyKey, 130);
        var broadcastRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        broadcastRow.Controls.AddRange([broadcastKey, Spacer(8), new Label { Text = "works when the window is not focused", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Padding = new Padding(0, 5, 0, 0) }]);
        AddField(panel, "Broadcast hotkey", broadcastRow);
        AddField(panel, "Reply hotkey", replyKey);
        var runInTray = new CheckBox { Text = "Keep running in the notification area when closed", Checked = settings.RunInNotificationArea, AutoSize = true, Font = UiStyles.BodyFont };
        var startWithWindows = new CheckBox { Text = "Start with Windows", Checked = settings.StartWithWindows, AutoSize = true };
        var windowOptions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        startWithWindows.Font = UiStyles.BodyFont;
        windowOptions.Controls.Add(runInTray); windowOptions.Controls.Add(startWithWindows);
        AddField(panel, "Window", windowOptions);
        var save = new Button { Text = "Save" }; StylePrimary(save);
        save.Click += (_, _) => SaveIdentityAudioSettings(runInTray.Checked, startWithWindows.Checked);
        card.Controls.Add(panel);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 16, 0, 0) };
        var back = new Button { Text = "Back to Talk" }; StyleSecondary(back); back.Click += (_, _) => ShowTalk();
        actions.Controls.AddRange([save, Spacer(8), back]);
        content.Controls.Add(card); content.Controls.Add(actions);
        return page;
    }

    private UserControl BuildDiagnosticsPage()
    {
        var page = Page("Diagnostics", "Diagnostics", "Live receive counters and the discovery/session log. Audio availability never controls discovery.", 860, out var content);
        var cards = new TableLayoutPanel { Width = 820, Height = 72, ColumnCount = 4, Margin = new Padding(0, 18, 0, 0) };
        for (var i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        foreach (var (title, i) in new[] { ("UDP audio", 0), ("Decoded frames", 1), ("PLC frames", 2), ("Output buffer", 3) })
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(i == 0 ? 0 : 6, 0, i == 3 ? 0 : 6, 0), BackColor = UiStyles.White, Padding = new Padding(16, 12, 16, 8) };
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Paint += (_, e) => UiStyles.DrawBorder(e, card);
            card.Controls.Add(new Label { Text = title.ToUpperInvariant(), AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Muted, Anchor = AnchorStyles.Left }, 0, 0);
            card.Controls.Add(new Label { Text = "—", AutoSize = true, Name = $"Diagnostic{i}", Font = new Font("Segoe UI", 16.5f, FontStyle.Bold), ForeColor = UiStyles.Ink, Margin = new Padding(0, 6, 0, 0), Anchor = AnchorStyles.Left }, 0, 1);
            cards.Controls.Add(card, i, 0);
        }
        diagnosticsCounters.Visible = false;
        var copy = PlainButton("Copy log");
        copy.Click += (_, _) => { try { Clipboard.SetText(ReadDiagnosticLog()); } catch { } };
        var save = PlainButton("Save log to file…");
        save.Click += (_, _) => SaveDiagnosticLog();
        var log = new RichTextBox { Width = 820, Height = 300, ReadOnly = true, DetectUrls = false, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(28, 31, 35), ForeColor = UiStyles.Disabled, Font = UiStyles.Log, ScrollBars = RichTextBoxScrollBars.Vertical, Padding = new Padding(16, 14, 16, 14), Margin = new Padding(0, 18, 0, 0) };
        AppendDiagnosticLines(log, ReadDiagnosticLog());
        content.Controls.Add(cards);
        content.Controls.Add(log);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        actions.Controls.AddRange([copy, Spacer(8), save]);
        content.Controls.Add(actions);
        return page;
    }

    private void ShowSettings(string page)
    {
        talkPage.Visible = false;
        settingsPage.Visible = true;
        nowBar.Visible = false;
        shell.RowStyles[1].Height = 0;
        activeSettingsPage = page;
        foreach (var (name, view) in settingsViews) view.Visible = name == page;
        System.Diagnostics.Debug.Assert(settingsContentHost.Controls.Count == 5 && settingsViews.Values.Count(view => view.Visible) == 1);
        UpdateSettingsNavigation();
        if (page == "Firmware") RefreshOtaQueue();
        UpdateSettingsStatus();
    }

    private void ShowTalk()
    {
        settingsPage.Visible = false;
        talkPage.Visible = true;
        nowBar.Visible = true;
        shell.RowStyles[1].Height = 76;
    }

    private void RefreshRedesignPeers(IReadOnlyList<Peer>? peers = null)
    {
        if (peers is not null) displayPeers = peers;
        var search = deviceFilter.Text.Trim();
        var active = displayPeers.Where(peer => (string.IsNullOrEmpty(search) || peer.Alias.Contains(search, StringComparison.OrdinalIgnoreCase) || peer.NodeId.ToString("x8").Contains(search, StringComparison.OrdinalIgnoreCase)) && MatchesActiveFilter(peer)).OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
        deviceCount.Text = $"{displayPeers.Count} of 16 in group {settings.MeshId}";
        UpdateFilterChips();
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
        LayoutDeviceCards();
        RefreshOfflineRows(activeIds);
        UpdateSettingsStatus();
    }

    private void RefreshOfflineRows(ISet<uint> activeIds)
    {
        offlineDevices.SuspendLayout();
        offlineDevices.Controls.Clear();
        var offline = knownDevices.Devices.Where(device => !activeIds.Contains(device.NodeId)).OrderBy(device => device.Alias).ToArray();
        offlineFrame.Visible = offline.Length > 0;
        if (offline.Length == 0) { offlineDevices.ResumeLayout(); return; }
        var width = Math.Max(600, deviceGrid.Parent?.ClientSize.Width ?? 600);
        var title = new TableLayoutPanel { Height = 48, Width = width, BackColor = UiStyles.White, Padding = new Padding(14, 0, 14, 0), ColumnCount = 2, Margin = Padding.Empty };
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); title.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var titleCopy = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        titleCopy.Controls.Add(new Label { Text = "Known, not responding", AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Ink, Margin = Padding.Empty });
        titleCopy.Controls.Add(new Label { Text = $"{offline.Length} {Plural(offline.Length, "device", "devices")} kept from earlier sessions — they return to the grid as soon as they announce", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(10, 1, 0, 0) });
        var all = PlainButton($"Remove all {CountWord(offline.Length)}…"); all.Click += (_, _) => RemoveAllOffline(activeIds, offline.Length);
        title.Controls.Add(titleCopy, 0, 0); title.Controls.Add(all, 1, 0);
        title.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, title.Height - 1, title.Width, title.Height - 1); };
        offlineDevices.Controls.Add(title);
        foreach (var device in offline)
        {
            var row = new TableLayoutPanel { Height = 34, Width = width, BackColor = UiStyles.White, Padding = new Padding(14, 0, 14, 0), ColumnCount = 5, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(new Panel { Size = new Size(6, 6), BackColor = Color.FromArgb(201, 204, 208), Anchor = AnchorStyles.Left }, 0, 0);
            row.Controls.Add(new Label { Text = device.Alias, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Font = new Font(UiStyles.SecondaryFont, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
            row.Controls.Add(new Label { Text = $"{device.NodeId:x8} · {device.LastAddress}", AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
            row.Controls.Add(new Label { Text = HumanAge(DateTimeOffset.UtcNow - device.LastSeen), AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Muted, Anchor = AnchorStyles.Left }, 3, 0);
            var remove = PlainButton("Remove…"); remove.Click += (_, _) => RemoveKnownDevice(device); row.Controls.Add(remove, 4, 0);
            row.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(242, 244, 245)); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
            offlineDevices.Controls.Add(row);
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
        UpdateNowBarAppearance();
        if (receiveSession is not null)
        {
            var statistics = receiveSession.Statistics;
            statusRight.Text = $"Running in the notification area · Ctrl+Alt+B to broadcast";
            networkLabel.Text = $"Discovery: listening on 239.255.42.99:45678 · audio 16 kHz mono ADPCM · UDP {statistics.AudioPackets:n0} · PLC {statistics.ConcealedFrames:n0} · buffer {audio?.BufferedMilliseconds ?? 0} ms";
        }
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
        if (label is not null) label.Text = $"{settings.MeshId} · {displayPeers.Count} {Plural(displayPeers.Count, "device", "devices")}, 1 companion";
        applyActiveGroup.Text = $"All {displayPeers.Count} active {Plural(displayPeers.Count, "device", "devices")}, one at a time (each reboots)";
        UpdateGroupButton();
    }

    private void UpdateIdentityPresentation()
    {
        identityLabel.Text = settings.Alias.ToUpperInvariant();
        if (identityStrip.Controls.Find("IdentityMeta", true).OfType<Label>().FirstOrDefault() is { } meta)
            meta.Text = $"ID {settings.NodeId:x8} · group {settings.MeshId} · {displayPeers.Count} of 16 devices";
        identityMicrophone.Text = $"MIC\n{TrimDeviceName((recordingDevice.SelectedItem as RecordingDevice)?.Name ?? "Not selected")}";
        identitySpeaker.Text = $"SPEAKER\n{TrimDeviceName((playbackDevice.SelectedItem as PlaybackDevice)?.Name ?? "Not selected")}";
    }

    private static string TrimDeviceName(string value)
    {
        value = value.Replace("Microphone (", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Headset Earphone (", "", StringComparison.OrdinalIgnoreCase).TrimEnd(')');
        return value.Length <= 23 ? value : $"{value[..20]}…";
    }

    private static Control Spacer(int width) => new Panel { Width = width, Height = 1, Margin = Padding.Empty };

    private Panel CreateFilterChip(string name)
    {
        var label = new Label { Text = name, AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Body, Margin = new Padding(9, 5, 0, 0), Cursor = Cursors.Hand };
        var count = new Label { Text = "0", AutoSize = true, Name = "Count", Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Muted, Margin = new Padding(5, 5, 9, 0), Cursor = Cursors.Hand };
        var chip = new Panel { Height = 26, AutoSize = true, BackColor = UiStyles.White, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 6, 0), Tag = name };
        chip.Controls.Add(label); chip.Controls.Add(count);
        label.Location = new Point(0, 0);
        count.Location = new Point(label.PreferredWidth + 5, 0);
        chip.Width = label.PreferredWidth + count.PreferredWidth + 23;
        EventHandler select = (_, _) => { activeFilter = name; RefreshRedesignPeers(); };
        chip.Click += select; label.Click += select; count.Click += select;
        chip.Paint += (_, e) =>
        {
            var active = name == activeFilter;
            chip.BackColor = active ? UiStyles.Ink : UiStyles.White;
            label.ForeColor = active ? UiStyles.White : UiStyles.Body;
            count.ForeColor = active ? UiStyles.Disabled : UiStyles.Muted;
            UiStyles.DrawBorder(e, chip, active ? UiStyles.Ink : UiStyles.ControlBorder);
        };
        filterChips[name] = chip;
        return chip;
    }

    private void UpdateFilterChips()
    {
        foreach (var (name, chip) in filterChips)
        {
            var count = name switch
            {
                "Speaking" => displayPeers.Count(peer => peer.IsTalking),
                "Muted" => displayPeers.Count(peer => peer.HardwareMuted),
                "Silenced" => displayPeers.Count(peer => peer.SoftMuted),
                "Legacy" => displayPeers.Count(peer => peer.ProtocolVersion is null or 1),
                _ => displayPeers.Count,
            };
            if (chip.Controls.Find("Count", false).OfType<Label>().FirstOrDefault() is { } label) label.Text = count.ToString();
            chip.Invalidate();
        }
    }

    private void LayoutDeviceCards()
    {
        var cards = deviceCards.Values.Where(card => card.Visible).ToArray();
        deviceGrid.SuspendLayout();
        deviceGrid.Controls.Clear();
        deviceGrid.ColumnStyles.Clear();
        deviceGrid.RowStyles.Clear();
        for (var column = 0; column < 3; column++) deviceGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        deviceGrid.RowCount = (cards.Length + 2) / 3;
        for (var row = 0; row < deviceGrid.RowCount; row++) deviceGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        foreach (var (card, index) in cards.Select((card, index) => (card, index)))
        {
            card.Dock = DockStyle.Fill;
            card.Height = 120;
            card.Margin = new Padding(5);
            deviceGrid.Controls.Add(card, index % 3, index / 3);
        }
        deviceGrid.ResumeLayout();
    }

    private void UpdateNowBarAppearance()
    {
        var (background, accent, dot) = statusLabel.ForeColor == UiStyles.Blue
            ? (UiStyles.BlueTint, UiStyles.Blue, UiStyles.Blue)
            : statusLabel.ForeColor == UiStyles.Purple
                ? (UiStyles.PurpleTint, UiStyles.Purple, UiStyles.Purple)
                : statusLabel.ForeColor == UiStyles.Amber
                    ? (UiStyles.AmberTint, UiStyles.Amber, UiStyles.Amber)
                    : statusLabel.ForeColor == UiStyles.Red || statusLabel.ForeColor == Color.Firebrick
                        ? (UiStyles.RedTint, UiStyles.Red, UiStyles.Red)
                        : (UiStyles.Surface, UiStyles.Green, UiStyles.Green);
        nowBar.BackColor = background;
        nowAccent.BackColor = accent;
        nowTitle.ForeColor = accent;
        identityDot.BackColor = dot;
    }

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;
    private static string CountWord(int count) => count switch { 0 => "zero", 1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", _ => count.ToString() };
    private static string HumanAge(TimeSpan age) => age < TimeSpan.FromSeconds(90) ? "just now"
        : age < TimeSpan.FromHours(1) ? $"not heard for {(int)age.TotalMinutes} minutes"
        : age < TimeSpan.FromDays(1) ? $"not heard for {(int)age.TotalHours} hours"
        : $"not heard for {(int)age.TotalDays} days";

    private static void AppendDiagnosticLines(RichTextBox log, string source)
    {
        foreach (var raw in source.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var timestamp = raw.Length >= 19 && DateTimeOffset.TryParse(raw[..Math.Min(raw.Length, 34)], out var parsed)
                ? parsed.ToLocalTime().ToString("HH:mm:ss") : DateTime.Now.ToString("HH:mm:ss");
            var detail = raw.Length >= 34 && raw[10] == 'T' ? raw[34..].TrimStart() : raw;
            var color = detail.Contains("error", StringComparison.OrdinalIgnoreCase) || detail.Contains("failed", StringComparison.OrdinalIgnoreCase) || detail.Contains("expire", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(239, 154, 154)
                : detail.Contains("audio", StringComparison.OrdinalIgnoreCase) || detail.Contains("claim", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(100, 181, 246)
                : detail.Contains("busy", StringComparison.OrdinalIgnoreCase) || detail.Contains("mute", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(255, 213, 79)
                : UiStyles.Disabled;
            log.SelectionColor = color;
            log.AppendText($"{timestamp}  {detail.ToUpperInvariant()}{Environment.NewLine}");
        }
        log.SelectionStart = 0;
        log.ScrollToCaret();
    }

    private int BrandIconFrame(int logicalPixels)
    {
        var target = (int)Math.Round(logicalPixels * DeviceDpi / 96.0);
        var bestFrame = 16;
        foreach (var frame in new[] { 20, 24, 32, 48 })
        {
            if (Math.Abs(frame - target) < Math.Abs(bestFrame - target)) bestFrame = frame;
        }

        return bestFrame;
    }

    private Label SettingsNavItem(string title, string page)
    {
        var item = new Label { Text = title, AutoSize = false, Dock = DockStyle.Top, Height = 40, Padding = new Padding(18, 0, 18, 0), Font = UiStyles.BodyFont, ForeColor = UiStyles.Body, Cursor = Cursors.Hand, BackColor = Color.Transparent, Tag = page, TextAlign = ContentAlignment.MiddleLeft };
        EventHandler select = (_, _) => ShowSettings(page);
        item.Click += select;
        item.Paint += (_, e) =>
        {
            if (page != activeSettingsPage) return;
            e.Graphics.FillRectangle(new SolidBrush(UiStyles.Blue), 0, 0, 3, item.Height);
        };
        return item;
    }

    private void UpdateSettingsNavigation()
    {
        foreach (var (page, item) in settingsNavItems)
        {
            var selected = page == activeSettingsPage;
            item.BackColor = selected ? UiStyles.White : Color.Transparent;
            item.Font = new Font(UiStyles.BodyFont, selected ? FontStyle.Bold : FontStyle.Regular);
            item.ForeColor = selected ? UiStyles.Ink : UiStyles.Body;
            item.Invalidate();
        }
    }

    private static TableLayoutPanel FormGrid(int labelWidth)
    {
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, GrowStyle = TableLayoutPanelGrowStyle.AddRows, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddField(TableLayoutPanel grid, string label, params Control[] fields)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Ink, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 16, 7) }, 0, row);
        var fieldPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        foreach (var field in fields) { field.Margin = new Padding(0, 2, 8, 8); fieldPanel.Controls.Add(field); }
        grid.Controls.Add(fieldPanel, 1, row);
    }

    private static void StylePrimary(Button button)
    {
        UiStyles.StylePrimary(button);
    }

    private static void StyleSecondary(Button button)
    {
        UiStyles.StyleButton(button, new Padding(10, 6, 10, 6));
    }

    private static void StyleDanger(Button button)
    {
        UiStyles.StyleDanger(button);
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
            otaQueue.Controls.Add(new Label { Text = "No devices queued. Add compatible active devices to update them sequentially.", AutoSize = true, ForeColor = UiStyles.Secondary, Font = UiStyles.SecondaryFont, Padding = new Padding(0, 14, 0, 14), Margin = Padding.Empty });
            return;
        }
        foreach (var peer in peers)
        {
            var row = new TableLayoutPanel { Width = 778, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = UiStyles.White, Padding = new Padding(0, 12, 0, 12), Margin = Padding.Empty };
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };

            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 0, 0, 0), Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new Label { Text = peer.Alias, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            header.Controls.Add(new Label { Text = $"{peer.FirmwareVersion}  →  selected package", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left }, 1, 0);
            header.Controls.Add(new Label { Text = "QUEUED", AutoSize = true, Font = UiStyles.Badge, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left }, 2, 0);
            var remove = PlainButton("Remove");
            remove.Click += (_, _) => { queuedOtaDevices.Remove(peer.NodeId); RefreshOtaQueue(); };
            header.Controls.Add(remove, 3, 0);

            var progress = new Panel { Dock = DockStyle.Fill, Height = 6, BackColor = Color.FromArgb(233, 235, 238), Margin = new Padding(0, 0, 0, 0) };
            var detail = new Label { Text = "Waiting for earlier devices", AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Muted, Margin = Padding.Empty };
            row.Controls.Add(header, 0, 0);
            row.Controls.Add(progress, 0, 1);
            row.Controls.Add(detail, 0, 2);
            otaQueue.Controls.Add(row);
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

    private void UpdateGroupButton()
    {
        var enabled = Protocol.IsValidMeshId(groupId.Text) && groupConfirmation.Text == settings.MeshId && (applyCompanionGroup.Checked || applyActiveGroup.Checked);
        applyGroup.Enabled = enabled;
        applyGroup.BackColor = enabled ? UiStyles.Red : Color.FromArgb(208, 138, 138);
        applyGroup.ForeColor = UiStyles.White;
        applyGroup.FlatAppearance.BorderColor = applyGroup.BackColor;
    }

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
        var row = new TableLayoutPanel { Width = Math.Max(280, activity.ClientSize.Width - SystemInformation.VerticalScrollBarWidth), Height = 32, ColumnCount = 3, Padding = new Padding(16, 0, 16, 0), Margin = Padding.Empty, BackColor = UiStyles.White };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var color = text.Contains("failed", StringComparison.OrdinalIgnoreCase) ? UiStyles.Red
            : text.Contains("busy", StringComparison.OrdinalIgnoreCase) || text.Contains("claim", StringComparison.OrdinalIgnoreCase) ? UiStyles.Amber
            : text.Contains("receiv", StringComparison.OrdinalIgnoreCase) ? UiStyles.Blue : UiStyles.Green;
        row.Controls.Add(new Label { Text = DateTime.Now.ToString("HH:mm"), AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Muted, Anchor = AnchorStyles.Left }, 0, 0);
        row.Controls.Add(new Panel { Size = new Size(8, 8), BackColor = color, Anchor = AnchorStyles.Left }, 1, 0);
        row.Controls.Add(new Label { Text = text, AutoEllipsis = true, Dock = DockStyle.Fill, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Body, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        row.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(242, 244, 245)); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
        activity.Controls.Add(row);
        activity.Controls.SetChildIndex(row, 0);
        while (activity.Controls.Count > 6) { var old = activity.Controls[^1]; activity.Controls.Remove(old); old.Dispose(); }
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
            trayIcon = new NotifyIcon
            {
                Text = "Intercom Companion",
                Icon = BrandAssets.AppIconAt(SystemInformation.SmallIconSize.Width),
                ContextMenuStrip = menu,
                Visible = true,
            };
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
