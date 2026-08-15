using IntercomCompanion.Core;

namespace IntercomCompanion;

/// <summary>One independently refreshable active-device card. It owns no network work.</summary>
internal sealed class DeviceCard : Panel
{
    private static readonly Color Purple = Color.FromArgb(106, 27, 154);
    private readonly Label alias = new() { AutoEllipsis = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private readonly Label badge = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 7, FontStyle.Bold), Padding = new Padding(4, 2, 4, 2) };
    private readonly Label details = new() { AutoEllipsis = true, ForeColor = Color.FromArgb(99, 103, 109), Font = new Font("Segoe UI", 7.5f) };
    private readonly Label mute = new() { AutoEllipsis = true, ForeColor = Color.FromArgb(99, 103, 109), Font = new Font("Segoe UI", 8) };
    private readonly Button talk = FlatButton("Hold to talk", Purple);
    private readonly Button silence = FlatButton("Silence", Color.White, Color.FromArgb(23, 25, 28));
    private readonly Button more = FlatButton("⋯", Color.White, Color.FromArgb(23, 25, 28));
    private readonly TrackBar volume = new() { Minimum = 64, Maximum = 1024, TickStyle = TickStyle.None, SmallChange = 16 };
    private readonly Label volumeValue = new() { AutoSize = true, ForeColor = Color.FromArgb(99, 103, 109), Font = new Font("Segoe UI", 8) };
    private Peer? peer;
    private bool updating;
    private float pulse;

    public DeviceCard()
    {
        Size = new Size(304, 151);
        Margin = new Padding(0, 0, 9, 10);
        BackColor = Color.White;
        Padding = new Padding(14, 10, 14, 9);
        DoubleBuffered = true;

        alias.SetBounds(14, 11, 182, 18);
        badge.Location = new Point(205, 10);
        details.SetBounds(14, 34, 267, 15);
        mute.SetBounds(14, 55, 267, 16);
        talk.SetBounds(14, 76, 180, 32);
        silence.SetBounds(198, 76, 54, 32);
        more.SetBounds(256, 76, 26, 32);
        volume.SetBounds(28, 118, 202, 25);
        volumeValue.SetBounds(242, 124, 40, 16);
        Controls.AddRange([alias, badge, details, mute, talk, silence, more, volume, volumeValue]);

        talk.MouseDown += (_, eventArgs) => { if (eventArgs.Button == MouseButtons.Left && peer is not null) TalkPressed?.Invoke(peer); };
        talk.MouseUp += (_, eventArgs) => { if (eventArgs.Button == MouseButtons.Left) TalkReleased?.Invoke(); };
        talk.MouseCaptureChanged += (_, _) => { if (!talk.Capture) TalkReleased?.Invoke(); };
        silence.Click += (_, _) => { if (peer is not null) SilenceClicked?.Invoke(peer); };
        more.Click += (_, _) => { if (peer is not null) MoreClicked?.Invoke(peer); };
        volume.MouseUp += (_, eventArgs) => { if (eventArgs.Button == MouseButtons.Left && peer is not null && !updating) VolumeCommitted?.Invoke(peer, volume.Value); };
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
        alias.Text = value.Alias;
        var (badgeText, badgeColor) = value.IsTalking ? ("SPEAKING", Color.FromArgb(21, 101, 192))
            : value.HardwareMuted ? ("MUTED", Color.FromArgb(249, 168, 37))
            : value.SoftMuted ? ("SOFT MUTED", Color.FromArgb(99, 103, 109))
            : value.ProtocolVersion is null or 1 ? ("LEGACY", Color.FromArgb(99, 103, 109))
            : ("IDLE", Color.FromArgb(46, 125, 50));
        badge.Text = badgeText;
        badge.BackColor = badgeColor;
        details.Text = $"{value.NodeId:x8} · {value.Endpoint.Address} · {value.FirmwareVersion} · {(value.ProtocolVersion is null ? "legacy" : $"p{value.ProtocolVersion}")}";
        mute.Text = value.HardwareMuted ? "● Mute slider on at the device"
            : value.SoftMuted ? "● Soft muted from here"
            : value.SupportsMuteReporting ? "● Playing received audio"
            : "● No mute reporting before p2";
        talk.Enabled = !pttDisabled;
        silence.Enabled = !pttDisabled && value.SupportsMuteReporting && !value.HardwareMuted;
        silence.Text = value.SoftMuted ? "Silenced" : "Silence";
        var silenceUnavailable = !silence.Enabled;
        silence.ForeColor = silenceUnavailable ? Color.FromArgb(154, 160, 166) : value.SoftMuted ? Color.White : Color.FromArgb(23, 25, 28);
        silence.BackColor = silenceUnavailable ? Color.FromArgb(245, 246, 247) : value.SoftMuted ? Color.FromArgb(99, 103, 109) : Color.White;
        silence.FlatAppearance.BorderColor = silenceUnavailable ? Color.FromArgb(220, 223, 227) : Color.FromArgb(173, 178, 184);
        silence.AccessibleDescription = value.HardwareMuted ? "Unavailable while the physical mute slider is on" : null;
        var correctedVolume = Math.Clamp(knownVolume, volume.Minimum, volume.Maximum);
        if (!volume.Capture) volume.Value = correctedVolume;
        volumeValue.Text = correctedVolume.ToString();
        updating = false;
        Invalidate();
    }

    public void SetPulse(float nextPulse)
    {
        pulse = nextPulse;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var talking = peer?.IsTalking == true;
        using var basePen = new Pen(talking ? Color.FromArgb(115, 174, 224) : Color.FromArgb(220, 223, 227), talking ? 3 : 1);
        eventArgs.Graphics.DrawRectangle(basePen, 1, 1, Width - 3, Height - 3);
        if (!talking) return;

        // A blue head travels around a permanently lit wider border. The card
        // therefore reads as speaking at a glance while still having motion.
        var perimeter = 2f * ((Width - 4) + (Height - 4));
        var head = (pulse % (MathF.PI * 2)) / (MathF.PI * 2) * perimeter;
        using var tracer = new Pen(Color.FromArgb(21, 101, 192), 3);
        DrawPerimeterSegment(eventArgs.Graphics, tracer, head, perimeter * 0.34f);
    }

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount), (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

    private void DrawPerimeterSegment(Graphics graphics, Pen pen, float start, float length)
    {
        var left = 2f; var top = 2f; var right = Width - 3f; var bottom = Height - 3f;
        var edges = new[]
        {
            (new PointF(left, top), new PointF(right, top), right - left),
            (new PointF(right, top), new PointF(right, bottom), bottom - top),
            (new PointF(right, bottom), new PointF(left, bottom), right - left),
            (new PointF(left, bottom), new PointF(left, top), bottom - top),
        };
        var perimeter = edges.Sum(edge => edge.Item3);
        var cursor = start % perimeter;
        var remaining = length;
        while (remaining > 0.01f)
        {
            var offset = 0f;
            foreach (var (from, to, edgeLength) in edges)
            {
                if (cursor >= offset + edgeLength) { offset += edgeLength; continue; }
                var fromOffset = Math.Max(0, cursor - offset);
                var draw = Math.Min(edgeLength - fromOffset, remaining);
                graphics.DrawLine(pen, Interpolate(from, to, fromOffset / edgeLength), Interpolate(from, to, (fromOffset + draw) / edgeLength));
                remaining -= draw;
                cursor = (cursor + draw) % perimeter;
                break;
            }
        }
    }

    private static PointF Interpolate(PointF from, PointF to, float amount) => new(
        from.X + (to.X - from.X) * amount, from.Y + (to.Y - from.Y) * amount);

    private static Button FlatButton(string text, Color background, Color? foreground = null) => new()
    {
        Text = text, BackColor = background, ForeColor = foreground ?? Color.White, FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), UseVisualStyleBackColor = false,
    };
}
