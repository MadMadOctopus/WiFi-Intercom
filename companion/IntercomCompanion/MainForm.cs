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
    private readonly ComboBox recordingDevice = new() { Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox playbackDevice = new() { Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button applyAudioDevices = new() { Text = "Apply audio devices", AutoSize = true };
    private readonly ComboBox usbPort = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button refreshUsbPorts = new() { Text = "Refresh ports", AutoSize = true };
    private readonly TextBox usbSsid = new() { Width = 150, MaxLength = 32 };
    private readonly TextBox usbPassword = new() { Width = 150, MaxLength = 64, UseSystemPasswordChar = true };
    private readonly Button getUsbConfig = new() { Text = "Read USB config", AutoSize = true };
    private readonly Button applyUsbWifi = new() { Text = "Apply Wi-Fi over USB", AutoSize = true };
    private readonly Label usbStatus = new() { AutoSize = true, ForeColor = Color.DimGray };
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
    // This is a local draft, not a view of the live device list. Discovery
    // must never lock or discard a user's in-progress settings edit.
    private readonly TextBox alias = new();
    private readonly NumericUpDown volume = new() { Minimum = 64, Maximum = 1024 };
    private readonly NumericUpDown brightness = new() { Minimum = 0, Maximum = 255 };
    private readonly CheckBox buttonsSwapped = new();
    private readonly ComboBox orientation = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button getConfig = new() { Text = "Get configuration", Enabled = false, AutoSize = true };
    private readonly Button applyConfig = new() { Text = "Apply configuration", Enabled = false, AutoSize = true };
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1000 };
    private bool spaceHeld;
    private uint? selectedDeviceId;
    private bool refreshingDeviceList;

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
        devices.SelectedIndexChanged += (_, _) =>
        {
            if (!refreshingDeviceList) OnSelectedDeviceChanged();
        };
        var deviceGroup = new GroupBox { Text = "Active devices", Dock = DockStyle.Fill };
        deviceGroup.Controls.Add(devices);

        orientation.Items.AddRange(["0", "180"]);
        orientation.SelectedItem = "0";
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
        var audioRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        audioRow.Controls.AddRange([
            new Label { Text = "Recording", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, recordingDevice,
            new Label { Text = "Playback", AutoSize = true, Padding = new Padding(8, 5, 0, 0) }, playbackDevice,
            applyAudioDevices]);
        applyAudioDevices.Click += (_, _) => ApplyAudioDeviceSelection();
        var usbRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        usbRow.Controls.AddRange([
            new Label { Text = "USB port", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, usbPort, refreshUsbPorts,
            new Label { Text = "Wi-Fi SSID", AutoSize = true, Padding = new Padding(8, 5, 0, 0) }, usbSsid,
            new Label { Text = "Password", AutoSize = true, Padding = new Padding(8, 5, 0, 0) }, usbPassword,
            getUsbConfig, applyUsbWifi]);
        refreshUsbPorts.Click += (_, _) => RefreshUsbPorts();
        getUsbConfig.Click += async (_, _) => await ReadUsbConfigurationAsync();
        applyUsbWifi.Click += async (_, _) => await ApplyUsbWifiAsync();
        top.Controls.AddRange([title, identityLabel, aliasRow, audioRow, usbRow, usbStatus, networkLabel, statusLabel, pttPanel]);
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
            node.Diagnostic += message => PostToUi(() => networkLabel.Text = $"Discovery: {message}");
            node.Start();
            networkLabel.Text = "Discovery: listening on 239.255.42.99:45678";
            PopulateAudioDevices();
            RefreshUsbPorts();
            StartAudioEngine();
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
        catch (Exception exception)
        {
            statusLabel.Text = $"Audio unavailable: {exception.Message}";
            statusLabel.ForeColor = Color.Firebrick;
        }
    }

    private void RefreshPeers()
    {
        if (node is null || IsDisposed) return;
        var selectedId = selectedDeviceId;
        var peers = node.Peers.OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
        refreshingDeviceList = true;
        try
        {
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
        }
        finally
        {
            refreshingDeviceList = false;
        }
        UpdateSelectedDeviceActions();
        if (receiveSession?.State == IntercomState.Receiving && audio is not null)
        {
            var stats = receiveSession.Statistics;
            statusLabel.Text = $"Receiving — UDP {stats.AudioPackets}, decoded {stats.DecodedFrames}, played {stats.PlayedFrames}, PLC {stats.ConcealedFrames}, gaps {stats.SequenceGaps}, output {audio.BufferedMilliseconds} ms";
        }
    }

    private Peer? SelectedPeer => selectedDeviceId is uint deviceId && node is not null
        ? node.Peers.FirstOrDefault(peer => peer.NodeId == deviceId)
        : null;

    private async Task RequestSelectedConfigAsync()
    {
        var peer = SelectedPeer;
        if (node is null || peer is null) return;
        getConfig.Enabled = false;
        try
        {
            var response = await node.RequestConfigurationAsync(peer);
            if (selectedDeviceId == response.NodeId) ShowConfiguration(response);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException)
        {
            networkLabel.Text = $"Configuration read failed: {exception.Message}";
        }
        finally { UpdateSelectedDeviceActions(); }
    }

    private async Task ApplySelectedConfigAsync()
    {
        var peer = SelectedPeer;
        if (node is null || peer is null) return;
        var configuration = new DeviceConfiguration(alias.Text.Trim(), (int)volume.Value,
            (int)brightness.Value, buttonsSwapped.Checked, int.Parse(orientation.Text));
        applyConfig.Enabled = false;
        try
        {
            var response = await node.SetConfigurationAsync(peer, configuration);
            if (selectedDeviceId == response.NodeId) ShowConfiguration(response);
            networkLabel.Text = $"Configuration applied to {peer.Alias}.";
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException)
        {
            networkLabel.Text = $"Configuration update failed: {exception.Message}";
        }
        finally { UpdateSelectedDeviceActions(); }
    }

    private void ShowConfiguration(DeviceConfigurationReceivedEventArgs eventArgs)
    {
        if (selectedDeviceId != eventArgs.NodeId) return;
        alias.Text = eventArgs.Configuration.Alias;
        volume.Value = Math.Clamp(eventArgs.Configuration.SpeakerVolume, (int)volume.Minimum, (int)volume.Maximum);
        brightness.Value = Math.Clamp(eventArgs.Configuration.LedBrightness, (int)brightness.Minimum, (int)brightness.Maximum);
        buttonsSwapped.Checked = eventArgs.Configuration.ButtonsSwapped;
        orientation.SelectedItem = eventArgs.Configuration.RingOrientation.ToString();
    }

    private void OnSelectedDeviceChanged()
    {
        selectedDeviceId = devices.SelectedItems.Count == 1
            ? (devices.SelectedItems[0].Tag as Peer)?.NodeId
            : null;
        UpdateSelectedDeviceActions();
        // Selecting a row intentionally does not perform network I/O. The user
        // explicitly chooses Get configuration when they want to populate it.
    }

    private void UpdateSelectedDeviceActions()
    {
        var hasSelectedPeer = SelectedPeer is not null;
        selected.Enabled = receiveSession is not null && hasSelectedPeer;
        getConfig.Enabled = hasSelectedPeer;
        applyConfig.Enabled = hasSelectedPeer;
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

    private void PopulateAudioDevices()
    {
        recordingDevice.Items.Clear();
        recordingDevice.Items.AddRange(AudioEngine.RecordingDevices().Cast<object>().ToArray());
        recordingDevice.SelectedItem = recordingDevice.Items.Cast<RecordingDevice>()
            .FirstOrDefault(device => device.Name == settings.RecordingDeviceName) ?? recordingDevice.Items.Cast<RecordingDevice>().FirstOrDefault();

        playbackDevice.Items.Clear();
        playbackDevice.Items.AddRange(AudioEngine.PlaybackDevices().Cast<object>().ToArray());
        playbackDevice.SelectedItem = playbackDevice.Items.Cast<PlaybackDevice>()
            .FirstOrDefault(device => device.Id == settings.PlaybackDeviceId) ?? playbackDevice.Items.Cast<PlaybackDevice>().FirstOrDefault();
    }

    private void StartAudioEngine()
    {
        audio = new AudioEngine(settings.RecordingDeviceName, settings.PlaybackDeviceId);
        audio.Diagnostic += message => PostToUi(() => statusLabel.Text = message);
        audio.StartPlayback();
        receiveSession = new ReceiveSession(node!, audio);
        receiveSession.StateChanged += sessionState => PostToUi(() => ShowIntercomState(sessionState));
        receiveSession.Diagnostic += message => PostToUi(() => statusLabel.Text = message);
        receiveSession.Start();
    }

    private void ApplyAudioDeviceSelection()
    {
        var recorder = recordingDevice.SelectedItem as RecordingDevice;
        var playback = playbackDevice.SelectedItem as PlaybackDevice;
        if (recorder is null || playback is null) return;
        if (receiveSession?.State != IntercomState.Idle)
        {
            statusLabel.Text = "Release the floor before changing audio devices.";
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
            statusLabel.Text = $"Audio devices applied: {recorder.Name} → {playback.Name}";
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Audio device change failed: {exception.Message}";
            statusLabel.ForeColor = Color.Firebrick;
        }
    }

    private void RefreshUsbPorts()
    {
        var current = usbPort.SelectedItem as string;
        var ports = UsbConfigurationClient.GetPortNames();
        usbPort.Items.Clear();
        usbPort.Items.AddRange(ports.Cast<object>().ToArray());
        usbPort.SelectedItem = ports.Contains(current, StringComparer.OrdinalIgnoreCase) ? current : ports.FirstOrDefault();
        usbStatus.Text = ports.Count == 0
            ? "USB provisioning: no serial ports found."
            : "USB provisioning: password stays on this PC and is never saved by the companion.";
    }

    private async Task ReadUsbConfigurationAsync()
    {
        var port = usbPort.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(port))
        {
            usbStatus.Text = "Choose the device's USB serial port first.";
            return;
        }
        SetUsbControlsEnabled(false);
        usbStatus.Text = $"Reading configuration from {port}…";
        try
        {
            var configuration = await UsbConfigurationClient.GetConfigurationAsync(port);
            usbSsid.Text = configuration.Ssid == "YOUR_WIFI_SSID" ? "" : configuration.Ssid;
            usbPassword.Clear();
            usbStatus.Text = $"Read {port}: {configuration.Alias} ({configuration.DeviceId:x8}). Passwords are never read back.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            usbStatus.Text = $"USB read failed: {exception.Message}";
        }
        finally
        {
            SetUsbControlsEnabled(true);
        }
    }

    private async Task ApplyUsbWifiAsync()
    {
        var port = usbPort.SelectedItem as string;
        var ssid = usbSsid.Text.Trim();
        if (string.IsNullOrWhiteSpace(port) || string.IsNullOrWhiteSpace(ssid))
        {
            usbStatus.Text = "Choose a USB port and enter the Wi-Fi SSID.";
            return;
        }
        SetUsbControlsEnabled(false);
        usbStatus.Text = $"Writing Wi-Fi configuration to {port}…";
        try
        {
            var configuration = await UsbConfigurationClient.SetWifiAsync(port, ssid, usbPassword.Text);
            usbPassword.Clear();
            usbStatus.Text = $"Wi-Fi saved for {configuration.Alias}. The device is rebooting onto the network…";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            usbStatus.Text = $"USB update failed: {exception.Message}";
        }
        finally
        {
            SetUsbControlsEnabled(true);
        }
    }

    private void SetUsbControlsEnabled(bool enabled)
    {
        usbPort.Enabled = refreshUsbPorts.Enabled = usbSsid.Enabled = usbPassword.Enabled =
            getUsbConfig.Enabled = applyUsbWifi.Enabled = enabled;
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
