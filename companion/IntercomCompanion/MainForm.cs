using IntercomCompanion.Core;
using System.Net.Sockets;
using IntercomCompanion.Audio;

namespace IntercomCompanion;

/// <summary>UI-only WinForms surface; all intercom work lives in Core.</summary>
internal sealed class MainForm : Form
{
    private readonly CompanionSettings settings = CompanionSettings.Load();
    private IntercomNode? node;
    private AudioEngine? audio;
    private ReceiveSession? receiveSession;
    private readonly Label identityLabel = new() { AutoSize = true };
    private readonly TextBox companionAlias = new() { Width = 180 };
    private readonly Button saveCompanionAlias = new() { Text = "Save companion alias", AutoSize = true };
    private readonly Label networkLabel = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label statusLabel = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 18, FontStyle.Bold),
        ForeColor = Color.DimGray,
        Text = "Starting…"
    };
    private readonly ListView devices = new()
    {
        Dock = DockStyle.Fill, FullRowSelect = true, GridLines = true,
        MultiSelect = false, View = View.Details
    };
    private readonly Button broadcast = CreatePttButton("Hold to broadcast", Color.FromArgb(46, 125, 50));
    private readonly Button reply = CreatePttButton("Hold to reply", Color.FromArgb(21, 101, 192));
    private readonly Button selected = CreatePttButton("Hold to selected device", Color.FromArgb(106, 27, 154));
    private readonly TextBox alias = new() { Enabled = false };
    private readonly NumericUpDown volume = new() { Enabled = false, Minimum = 64, Maximum = 1024 };
    private readonly NumericUpDown brightness = new() { Enabled = false, Minimum = 0, Maximum = 255 };
    private readonly CheckBox buttonsSwapped = new() { Enabled = false };
    private readonly ComboBox orientation = new() { Enabled = false, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button getConfig = new() { Text = "Get configuration", Enabled = false, AutoSize = true };
    private readonly Button applyConfig = new() { Text = "Apply configuration", Enabled = false, AutoSize = true };
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1000 };
    private bool spaceHeld;
    private uint? pendingConfigurationNodeId;

    public MainForm()
    {
        Text = "Wi-Fi Intercom Companion";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);
        Size = new Size(960, 650);
        Font = new Font("Segoe UI", 9);

        var title = new Label
        {
            Text = "Wi-Fi Intercom Companion",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            AutoSize = true,
        };
        identityLabel.Text = $"Companion ID: {settings.NodeId:x8}";
        companionAlias.Text = settings.Alias;
        networkLabel.Text = "Discovery: starting…";

        broadcast.Enabled = reply.Enabled = selected.Enabled = false;
        BindPtt(broadcast, () => receiveSession?.PressBroadcast());
        BindPtt(reply, () => receiveSession?.PressReply());
        BindPtt(selected, () =>
        {
            var peer = SelectedPeer;
            if (peer is not null) receiveSession?.PressSelected(peer);
        });
        var pttPanel = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = new Padding(0, 8, 0, 8),
        };
        pttPanel.Controls.AddRange([broadcast, reply, selected]);

        devices.Columns.Add("Alias", 150);
        devices.Columns.Add("Device ID", 110);
        devices.Columns.Add("Address", 135);
        devices.Columns.Add("Last seen", 90);
        devices.SelectedIndexChanged += (_, _) => OnSelectedDeviceChanged();
        var deviceGroup = new GroupBox { Text = "Active devices", Dock = DockStyle.Fill };
        deviceGroup.Controls.Add(devices);

        orientation.Items.AddRange(["0", "180"]);
        var config = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Padding = new Padding(8) };
        config.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddConfigField(config, "Alias", alias);
        AddConfigField(config, "Volume", volume);
        AddConfigField(config, "Ring brightness", brightness);
        AddConfigField(config, "Buttons swapped", buttonsSwapped);
        AddConfigField(config, "Ring orientation", orientation);
        var configActions = new FlowLayoutPanel { AutoSize = true };
        configActions.Controls.AddRange([getConfig, applyConfig]);
        config.SetColumnSpan(configActions, 2);
        config.Controls.Add(configActions, 0, config.RowCount++);
        getConfig.Click += async (_, _) => await RequestSelectedConfigAsync();
        applyConfig.Click += async (_, _) => await ApplySelectedConfigAsync();
        var configGroup = new GroupBox { Text = "Selected device configuration", Dock = DockStyle.Fill };
        configGroup.Controls.Add(config);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 0, 12, 12) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        body.Controls.Add(deviceGroup, 0, 0);
        body.Controls.Add(configGroup, 1, 0);

        var top = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Dock = DockStyle.Top, Padding = new Padding(12, 12, 12, 0),
        };
        var aliasRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        aliasRow.Controls.AddRange([new Label { Text = "Companion alias", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, companionAlias, saveCompanionAlias]);
        saveCompanionAlias.Click += (_, _) => SaveCompanionAlias();
        top.Controls.AddRange([title, identityLabel, aliasRow, networkLabel, statusLabel, pttPanel]);
        Controls.Add(body);
        Controls.Add(top);

        Shown += OnShown;
        FormClosing += OnFormClosing;
        refreshTimer.Tick += (_, _) => RefreshPeers();
        KeyPreview = true;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    private void OnShown(object? sender, EventArgs e)
    {
        try
        {
            settings.Save();
            node = new IntercomNode(settings);
            node.PeersChanged += (_, _) => PostToUi(RefreshPeers);
            node.ConfigurationReceived += (_, eventArgs) => PostToUi(() => ShowConfiguration(eventArgs));
            node.Diagnostic += message => PostToUi(() => networkLabel.Text = $"Discovery: {message}");
            node.Start();
            networkLabel.Text = "Discovery: listening on 239.255.42.99:45678";
            audio = new AudioEngine();
            audio.Diagnostic += message => PostToUi(() => statusLabel.Text = message);
            audio.StartPlayback();
            receiveSession = new ReceiveSession(node, audio);
            receiveSession.StateChanged += sessionState => PostToUi(() => ShowIntercomState(sessionState));
            receiveSession.Diagnostic += message => PostToUi(() => statusLabel.Text = message);
            receiveSession.Start();
            statusLabel.Text = "Idle — ready to receive";
            broadcast.Enabled = reply.Enabled = true;
            selected.Enabled = SelectedPeer is not null;
            refreshTimer.Start();
        }
        catch (SocketException exception)
        {
            networkLabel.Text = $"Discovery unavailable: {exception.SocketErrorCode}";
            statusLabel.Text = "Network unavailable";
            statusLabel.ForeColor = Color.Firebrick;
        }
    }

    private void RefreshPeers()
    {
        if (node is null || IsDisposed) return;
        var selectedId = SelectedPeer?.NodeId;
        var peers = node.Peers.OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
        devices.BeginUpdate();
        devices.Items.Clear();
        foreach (var peer in peers)
        {
            var age = DateTimeOffset.UtcNow - peer.LastSeen;
            var item = new ListViewItem([peer.Alias, peer.NodeId.ToString("x8"), peer.Endpoint.Address.ToString(), $"{Math.Max(0, age.TotalSeconds):0}s"])
            {
                Tag = peer
            };
            devices.Items.Add(item);
            if (peer.NodeId == selectedId) item.Selected = true;
        }
        devices.EndUpdate();
        if (receiveSession?.State == IntercomState.Receiving && audio is not null)
        {
            var stats = receiveSession.Statistics;
            statusLabel.Text = $"Receiving — UDP {stats.AudioPackets}, decoded {stats.DecodedFrames}, played {stats.PlayedFrames}, PLC {stats.ConcealedFrames}, gaps {stats.SequenceGaps}, output {audio.BufferedMilliseconds} ms";
        }
    }

    private Peer? SelectedPeer => devices.SelectedItems.Count == 1
        ? devices.SelectedItems[0].Tag as Peer : null;

    private async Task RequestSelectedConfigAsync()
    {
        var peer = SelectedPeer;
        if (node is null || peer is null) return;
        pendingConfigurationNodeId = peer.NodeId;
        try { await node.RequestConfigurationAsync(peer); }
        catch (SocketException exception) { networkLabel.Text = $"Configuration request failed: {exception.SocketErrorCode}"; }
    }

    private async Task ApplySelectedConfigAsync()
    {
        var peer = SelectedPeer;
        if (node is null || peer is null) return;
        var configuration = new DeviceConfiguration(alias.Text.Trim(), (int)volume.Value,
            (int)brightness.Value, buttonsSwapped.Checked, int.Parse(orientation.Text));
        try { await node.SetConfigurationAsync(peer, configuration); }
        catch (SocketException exception) { networkLabel.Text = $"Configuration update failed: {exception.SocketErrorCode}"; }
    }

    private void ShowConfiguration(DeviceConfigurationReceivedEventArgs eventArgs)
    {
        // A device can reply to another app's request or to an older selected
        // row. Never overwrite in-progress edits unless this exact UI asked
        // for the configuration of the still-selected peer.
        if (pendingConfigurationNodeId != eventArgs.NodeId || SelectedPeer?.NodeId != eventArgs.NodeId) return;
        pendingConfigurationNodeId = null;
        SetConfigurationControlsEnabled(true);
        getConfig.Enabled = applyConfig.Enabled = true;
        alias.Text = eventArgs.Configuration.Alias;
        volume.Value = Math.Clamp(eventArgs.Configuration.SpeakerVolume, (int)volume.Minimum, (int)volume.Maximum);
        brightness.Value = Math.Clamp(eventArgs.Configuration.LedBrightness, (int)brightness.Minimum, (int)brightness.Maximum);
        buttonsSwapped.Checked = eventArgs.Configuration.ButtonsSwapped;
        orientation.SelectedItem = eventArgs.Configuration.RingOrientation.ToString();
    }

    private void OnSelectedDeviceChanged()
    {
        pendingConfigurationNodeId = null;
        selected.Enabled = receiveSession is not null && SelectedPeer is not null;
        getConfig.Enabled = SelectedPeer is not null;
        applyConfig.Enabled = false;
        SetConfigurationControlsEnabled(false);
        // Selecting a row intentionally does not perform network I/O. The user
        // explicitly chooses Get configuration when they want to populate it.
    }

    private void SetConfigurationControlsEnabled(bool enabled)
    {
        alias.Enabled = volume.Enabled = brightness.Enabled = buttonsSwapped.Enabled = orientation.Enabled = enabled;
    }

    private void PostToUi(Action action)
    {
        if (!IsDisposed && IsHandleCreated)
            BeginInvoke(action);
    }

    private void ShowIntercomState(IntercomState sessionState)
    {
        (statusLabel.Text, statusLabel.ForeColor) = sessionState switch
        {
            IntercomState.Claiming => ("Claiming floor…", Color.FromArgb(249, 168, 37)),
            IntercomState.Talking => ("Talking", Color.FromArgb(46, 125, 50)),
            IntercomState.Receiving => ("Receiving", Color.FromArgb(21, 101, 192)),
            IntercomState.WaitingForFloor => ("Floor occupied — buffering up to 500 ms", Color.FromArgb(249, 168, 37)),
            _ => ("Idle — ready to receive", Color.DimGray),
        };
    }

    private void BindPtt(Button button, Action press)
    {
        button.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Left) return;
            button.Capture = true;
            press();
        };
        button.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left) receiveSession?.ReleasePtt();
        };
        button.MouseCaptureChanged += (_, _) =>
        {
            if (!button.Capture) receiveSession?.ReleasePtt();
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode != Keys.Space || spaceHeld) return;
        spaceHeld = true;
        receiveSession?.PressBroadcast();
        eventArgs.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode != Keys.Space) return;
        spaceHeld = false;
        receiveSession?.ReleasePtt();
        eventArgs.Handled = true;
    }

    private void SaveCompanionAlias()
    {
        if (node is null) return;
        node.SetAlias(companionAlias.Text);
        companionAlias.Text = node.Alias;
        identityLabel.Text = $"Companion ID: {node.NodeId:x8}";
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        refreshTimer.Stop();
        // Never wait for a timer/socket/audio worker from the UI close path.
        // Cancellation is enough; process shutdown cleans up background tasks.
        receiveSession?.Stop();
        audio?.Dispose();
        node?.Stop();
    }

    private static Button CreatePttButton(string text, Color color) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(0, 0, 8, 0),
        Padding = new Padding(12, 8, 12, 8), UseVisualStyleBackColor = false,
    };

    private static void AddConfigField(TableLayoutPanel panel, string label, Control field)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, panel.RowCount);
        field.Dock = DockStyle.Fill;
        panel.Controls.Add(field, 1, panel.RowCount++);
    }
}
