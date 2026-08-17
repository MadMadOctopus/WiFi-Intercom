using IntercomCompanion.Core;

namespace IntercomCompanion.Dialogs;

/// <summary>The per-device Configure dialog: alias, volume/brightness sliders,
/// playback state, button/ring-centre toggles and device ID, with restart notes
/// and a local-until-Apply contract.</summary>
internal sealed class ConfigureDeviceDialog : Form
{
    private readonly Peer peer;
    private readonly Func<uint, bool> isDuplicateId;

    private readonly TextBox aliasBox = new() { MaxLength = 24 };
    private readonly FlatSlider volume = new() { Minimum = 64, Maximum = 1024, Width = 230, AccentColor = UiStyles.Blue };
    private readonly Label volumeValue = new() { AutoSize = false, Width = 96, TextAlign = ContentAlignment.MiddleRight, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Body, Anchor = AnchorStyles.Right };
    private readonly FlatSlider brightness = new() { Minimum = 0, Maximum = 255, Width = 230, AccentColor = UiStyles.Amber };
    private readonly Label brightnessValue = new() { AutoSize = false, Width = 96, TextAlign = ContentAlignment.MiddleRight, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Body, Anchor = AnchorStyles.Right };
    private readonly SegmentToggle playback;
    private readonly SegmentToggle buttons;
    private readonly SegmentToggle ringCentre;
    private readonly TextBox deviceIdBox = new() { MaxLength = 10 };

    private DeviceConfiguration current;

    public ConfigureDeviceDialog(Peer peer, DeviceConfiguration configuration, Func<uint, bool> isDuplicateId)
    {
        this.peer = peer;
        this.isDuplicateId = isDuplicateId;
        current = configuration;

        Text = $"Configure {peer.Alias}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiStyles.White;
        Font = UiStyles.SecondaryFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        playback = new SegmentToggle(["Plays received audio", "Soft muted"], configuration.SoftMute ? 1 : 0, configuration.HardwareMuted);
        buttons = new SegmentToggle(["Standard", "Swapped"], configuration.ButtonsSwapped ? 1 : 0);
        ringCentre = new SegmentToggle(["0°", "180°"], configuration.RingOrientation == 180 ? 1 : 0);

        UiStyles.StyleInput(aliasBox, 240);
        UiStyles.StyleInput(deviceIdBox, 140);
        deviceIdBox.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Width = 520, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, BackColor = UiStyles.White };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(configuration), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);

        Populate(configuration);
    }

    public DeviceConfiguration? Result { get; private set; }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 1, Width = 520, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = UiStyles.White, Padding = new Padding(18, 14, 18, 14), Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        row.Controls.Add(new Label { Text = $"Configure {peer.Alias}", AutoSize = true, Font = UiStyles.PanelHeading, ForeColor = UiStyles.Ink, Margin = Padding.Empty });
        row.Controls.Add(new Label { Text = $"{peer.NodeId:x8} · {peer.Endpoint.Address}", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(10, 5, 0, 0) });
        header.Controls.Add(row, 0, 0);
        header.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
        return header;
    }

    private Control BuildBody(DeviceConfiguration configuration)
    {
        var body = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 520, BackColor = UiStyles.White, Padding = new Padding(18, 18, 18, 18), Margin = Padding.Empty };

        var intro = new Label
        {
            Text = "Read from the device. Your edits are not overwritten by discovery.",
            AutoSize = true, MaximumSize = new Size(484, 0), Font = UiStyles.SecondaryFont, ForeColor = UiStyles.DeepBlue,
            BackColor = UiStyles.BlueTint, Padding = new Padding(12, 9, 12, 9), Margin = new Padding(0, 0, 0, 6),
        };
        intro.Paint += (_, e) => { using var pen = new Pen(UiStyles.BlueBorder); e.Graphics.DrawRectangle(pen, 0, 0, intro.Width - 1, intro.Height - 1); };
        body.Controls.Add(intro);

        var form = UiKit.FormGrid(150);
        UiKit.AddField(form, "Alias", aliasBox);

        volume.ValueChanged += (_, _) => volumeValue.Text = $"{volume.Value} / 1024";
        UiKit.AddField(form, "Speaker volume", volume, volumeValue);
        brightness.ValueChanged += (_, _) => brightnessValue.Text = $"{brightness.Value} / 255";
        UiKit.AddField(form, "Ring brightness", brightness, brightnessValue);

        var playbackNote = new Label
        {
            Text = configuration.HardwareMuted
                ? "Device reports its mute slider on. Soft mute is unavailable while the physical switch is on."
                : "Device reports its mute slider off. Soft mute stops playback only; discovery and sending continue.",
            AutoSize = true, MaximumSize = new Size(330, 0), Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, Margin = new Padding(0, 6, 0, 0),
        };
        var playbackStack = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        playbackStack.Controls.Add(playback);
        playbackStack.Controls.Add(playbackNote);
        UiKit.AddField(form, "Playback", playbackStack);

        UiKit.AddField(form, "Buttons", buttons, UiKit.RestartNote());
        UiKit.AddField(form, "Ring centre", ringCentre, UiKit.RestartNote());
        UiKit.AddField(form, "Device ID", deviceIdBox, UiKit.RestartNote());
        body.Controls.Add(form);

        var warning = new Label
        {
            Text = "Wi-Fi credentials, ring brightness, button mapping, ring centre and device ID make the device restart after it acknowledges. Alias and volume apply immediately.",
            AutoSize = true, MaximumSize = new Size(484, 0), Font = UiStyles.SecondaryFont, ForeColor = UiStyles.AmberInk,
            BackColor = UiStyles.AmberTint, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0, 16, 0, 0),
        };
        warning.Paint += (_, e) => { using var pen = new Pen(UiStyles.AmberBorder); e.Graphics.DrawRectangle(pen, 0, 0, warning.Width - 1, warning.Height - 1); };
        body.Controls.Add(warning);
        return body;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 1, Width = 520, Height = 60, BackColor = UiStyles.DialogFooter, Padding = new Padding(18, 0, 18, 0), Margin = Padding.Empty };
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var apply = UiKit.PrimaryButton("Apply to device");
        apply.Anchor = AnchorStyles.Left;
        apply.Margin = new Padding(0, 0, 8, 0);
        apply.Click += OnApply;
        var cancel = UiKit.PlainButton("Cancel");
        cancel.Anchor = AnchorStyles.Left;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.Add(apply, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(new Label { Text = "Nothing is sent until you apply.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Right }, 2, 0);
        footer.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        AcceptButton = apply;
        return footer;
    }

    private void Populate(DeviceConfiguration configuration)
    {
        aliasBox.Text = configuration.Alias;
        volume.Value = Math.Clamp(configuration.SpeakerVolume, volume.Minimum, volume.Maximum);
        volumeValue.Text = $"{volume.Value} / 1024";
        brightness.Value = Math.Clamp(configuration.LedBrightness, brightness.Minimum, brightness.Maximum);
        brightnessValue.Text = $"{brightness.Value} / 255";
        deviceIdBox.Text = configuration.DeviceId.ToString();
    }

    private void OnApply(object? sender, EventArgs e)
    {
        if (!uint.TryParse(deviceIdBox.Text, out var requestedId) || requestedId == 0)
        {
            MessageBox.Show(this, "Device ID must be a non-zero whole number.", "Invalid device ID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (requestedId != peer.NodeId && isDuplicateId(requestedId))
        {
            MessageBox.Show(this, "That device ID is already held by a visible device. Nothing was sent.", "Duplicate device ID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result = current with
        {
            Alias = aliasBox.Text.Trim(),
            SpeakerVolume = volume.Value,
            LedBrightness = brightness.Value,
            SoftMute = current.HardwareMuted ? current.SoftMute : playback.SelectedIndex == 1,
            ButtonsSwapped = buttons.SelectedIndex == 1,
            RingOrientation = ringCentre.SelectedIndex == 1 ? 180 : 0,
            DeviceId = requestedId,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
