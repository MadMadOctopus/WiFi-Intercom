using IntercomCompanion.Core;

namespace IntercomCompanion;

/// <summary>One refreshable device card. Network operations are delegated through events.</summary>
internal sealed class DeviceCard : Panel
{
    private readonly Label alias = new() { AutoEllipsis = true, Font = UiStyles.CardAlias, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label badge = new() { AutoSize = true, ForeColor = Color.White, Font = UiStyles.Badge, Padding = new Padding(6, 2, 6, 2), Margin = Padding.Empty, Anchor = AnchorStyles.Right };
    private readonly Label details = new() { AutoEllipsis = true, ForeColor = UiStyles.Muted, Font = UiStyles.Meta, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Panel muteMarker = new() { Size = new Size(6, 6), Anchor = AnchorStyles.Left };
    private readonly Label muteNote = new() { AutoEllipsis = true, Font = UiStyles.Meta, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button talk = new() { Text = "Hold to talk", Dock = DockStyle.Fill };
    private readonly Button silence = new() { Text = "Silence", AutoSize = false, Dock = DockStyle.Fill };
    private readonly Button more = new() { Text = "⋯", AutoSize = false, Dock = DockStyle.Fill };
    private readonly FlatSlider volume = new() { Dock = DockStyle.Fill };
    private readonly Label volumeValue = new() { AutoSize = false, Width = 34, ForeColor = UiStyles.Body, Font = UiStyles.Meta, TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Right };
    private Peer? peer;
    private bool updating;
    private float pulse;
    private Color accent = UiStyles.Green;

    public DeviceCard()
    {
        // The grid owns placement: Dock = Fill, a fixed row height and a 5 px
        // margin. The card never carries an absolute Size of its own.
        Dock = DockStyle.Fill;
        Margin = new Padding(5);
        BackColor = UiStyles.White;
        Padding = new Padding(15, 10, 12, 10);
        DoubleBuffered = true;

        UiStyles.StylePrimary(talk, UiStyles.Purple);
        talk.AutoSize = false;
        talk.Font = UiStyles.SecondaryFont;
        talk.Padding = Padding.Empty;
        UiStyles.StyleButton(silence, Padding.Empty);
        silence.AutoSize = false;
        UiStyles.StyleButton(more, Padding.Empty);
        more.AutoSize = false;
        more.Font = UiStyles.CardOverflow;
        more.Padding = Padding.Empty;

        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty, Padding = Padding.Empty };
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 15));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 9));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        var aliasRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        aliasRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        aliasRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        aliasRow.Controls.Add(alias, 0, 0);
        aliasRow.Controls.Add(badge, 1, 0);

        var muteRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 2, 0, 0) };
        muteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 11));
        muteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        muteRow.Controls.Add(muteMarker, 0, 0);
        muteRow.Controls.Add(muteNote, 1, 0);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 31));
        talk.Margin = Padding.Empty;
        silence.Margin = new Padding(5, 0, 0, 0);
        more.Margin = new Padding(5, 0, 0, 0);
        buttons.Controls.Add(talk, 0, 0);
        buttons.Controls.Add(silence, 1, 0);
        buttons.Controls.Add(more, 2, 0);

        var volumeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        volumeRow.Controls.Add(new Label { Text = "Vol", Font = UiStyles.Meta, ForeColor = UiStyles.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        volumeRow.Controls.Add(volume, 1, 0);
        volumeRow.Controls.Add(volumeValue, 2, 0);

        rows.Controls.Add(aliasRow, 0, 0);
        rows.Controls.Add(details, 0, 1);
        rows.Controls.Add(muteRow, 0, 2);
        rows.Controls.Add(buttons, 0, 3);
        rows.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = UiStyles.RowRule, Margin = new Padding(0, 8, 0, 0) }, 0, 4);
        rows.Controls.Add(volumeRow, 0, 5);
        Controls.Add(rows);

        talk.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && peer is not null) TalkPressed?.Invoke(peer); };
        talk.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) TalkReleased?.Invoke(); };
        talk.MouseCaptureChanged += (_, _) => { if (!talk.Capture) TalkReleased?.Invoke(); };
        silence.Click += (_, _) => { if (peer is not null) SilenceClicked?.Invoke(peer); };
        more.Click += (_, _) => { if (peer is not null) MoreClicked?.Invoke(peer); };
        volume.ValueCommitted += (_, _) => { if (peer is not null && !updating) VolumeCommitted?.Invoke(peer, volume.Value); };
    }

    public event Action<Peer>? TalkPressed;
    public event Action? TalkReleased;
    public event Action<Peer>? SilenceClicked;
    public event Action<Peer>? MoreClicked;
    public event Action<Peer, int>? VolumeCommitted;

    public void UpdatePeer(Peer value, int knownVolume, bool pttDisabled)
    {
        peer = value;
        updating = true;
        var previousAccent = accent;
        accent = value.IsTalking ? UiStyles.Blue
            : value.HardwareMuted || value.IsLegacy ? UiStyles.Amber
            : value.SoftMuted ? UiStyles.Body : UiStyles.Green;
        alias.Text = value.Alias;
        var state = value.IsTalking ? "SPEAKING" : value.HardwareMuted ? "MUTED"
            : value.SoftMuted ? "SOFT MUTED" : value.IsLegacy ? "LEGACY" : "IDLE";
        badge.Text = state;
        badge.BackColor = accent;
        details.Text = $"{value.NodeId:x8} · {value.Endpoint.Address} · {value.FirmwareVersion} · {(value.ProtocolVersion is null ? "legacy" : $"p{value.ProtocolVersion}")}";
        muteMarker.BackColor = value.IsTalking ? UiStyles.Blue : value.HardwareMuted ? UiStyles.Amber
            : value.SoftMuted ? UiStyles.Body : value.SupportsMuteReporting ? UiStyles.Green : UiStyles.OfflineDot;
        muteNote.ForeColor = value.IsTalking ? UiStyles.Blue : value.HardwareMuted ? UiStyles.AmberInk
            : value.SoftMuted ? UiStyles.Body : UiStyles.Muted;
        muteNote.Text = value.IsTalking ? "Holding the floor" : value.HardwareMuted ? "Mute slider on at the device"
            : value.SoftMuted ? "Soft muted from here" : value.SupportsMuteReporting ? "Playing received audio"
            : "No mute reporting before p2";

        talk.Enabled = !pttDisabled;
        silence.Enabled = !pttDisabled && value.SupportsMuteReporting && !value.HardwareMuted;
        silence.Text = value.SoftMuted ? "Silenced" : "Silence";
        silence.ForeColor = !silence.Enabled ? UiStyles.Disabled : value.SoftMuted ? UiStyles.White : UiStyles.Body;
        silence.BackColor = !silence.Enabled ? UiStyles.HairRule : value.SoftMuted ? UiStyles.Body : UiStyles.White;
        silence.FlatAppearance.BorderColor = !silence.Enabled ? UiStyles.Border : UiStyles.ControlBorder;
        silence.AccessibleDescription = value.HardwareMuted ? "Unavailable while the physical mute slider is on" : null;

        volume.Enabled = !pttDisabled;
        volume.AccentColor = accent;
        volume.Value = Math.Clamp(knownVolume, volume.Minimum, volume.Maximum);
        volumeValue.Text = knownVolume.ToString();
        updating = false;
        // The painted parts (accent bar, animated border) only depend on accent
        // and the pulse phase; repaint just those cases so an idle 1 s refresh of
        // sixteen cards does not force sixteen full repaints.
        if (accent != previousAccent) Invalidate();
    }

    public void SetPulse(float nextPulse)
    {
        pulse = nextPulse;
        if (peer?.IsTalking == true) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var border = peer?.IsTalking == true
            ? UiKit.Blend(UiStyles.Border, UiStyles.Blue, (MathF.Sin(pulse) + 1f) / 2f)
            : UiStyles.Border;
        using var pen = new Pen(border);
        eventArgs.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using var accentBrush = new SolidBrush(accent);
        eventArgs.Graphics.FillRectangle(accentBrush, 0, 0, 3, Height);
        if (peer?.IsTalking != true) return;
        using var halo = new Pen(UiStyles.TalkingHalo, 2);
        eventArgs.Graphics.DrawRectangle(halo, 1, 1, Width - 3, Height - 3);
    }
}
