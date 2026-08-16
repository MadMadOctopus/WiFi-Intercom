using IntercomCompanion.Core;

namespace IntercomCompanion.Views;

/// <summary>The "Known, not responding" panel below the grid. Rows are the only
/// children rebuilt on refresh, and the whole panel hides when nothing is known
/// and offline.</summary>
internal sealed class KnownDevicesView : UserControl
{
    private readonly TableLayoutPanel body = new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1, BackColor = UiStyles.White, Margin = Padding.Empty, Padding = Padding.Empty,
    };

    public KnownDevicesView()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = UiStyles.White;
        Margin = new Padding(0, 16, 0, 0);
        Visible = false;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(body);
    }

    private string layoutKey = "";

    public event Action<KnownDevice>? RemoveClicked;
    public event Action<int>? RemoveAllClicked;

    public void Update(IReadOnlyList<KnownDevice> offline)
    {
        Visible = offline.Count > 0;
        // Rebuild only when the visible content actually changes; refreshing the
        // rows every second otherwise flickers the panel. The humanised age is
        // part of the key so it still refreshes when it ticks over (≈once/min).
        var ordered = offline.OrderBy(device => device.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
        var key = string.Join(";", ordered.Select(device => $"{device.NodeId:x8}|{device.Alias}|{HumanAge(DateTimeOffset.UtcNow - device.LastSeen)}"));
        if (key == layoutKey) return;
        layoutKey = key;

        body.SuspendLayout();
        body.Controls.Clear();
        body.RowStyles.Clear();
        body.RowCount = 0;
        if (offline.Count == 0) { body.ResumeLayout(); return; }

        AddRow(BuildHeader(offline.Count));
        foreach (var device in ordered) AddRow(BuildRow(device));
        body.ResumeLayout();
    }

    private void AddRow(Control row)
    {
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(row, 0, body.RowCount++);
    }

    private Control BuildHeader(int offlineCount)
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 3, BackColor = UiStyles.White, Padding = new Padding(14, 0, 14, 0), Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Known, not responding", AutoSize = true, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = $"{offlineCount} {Pluralize(offlineCount, "device", "devices")} kept from earlier sessions — they return to the grid as soon as they announce",
            AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left,
        }, 1, 0);
        var removeAll = UiKit.PlainButton($"Remove all {CountWord(offlineCount)}…");
        removeAll.Anchor = AnchorStyles.Right;
        removeAll.Click += (_, _) => RemoveAllClicked?.Invoke(offlineCount);
        header.Controls.Add(removeAll, 2, 0);
        header.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
        return header;
    }

    private Control BuildRow(KnownDevice device)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, ColumnCount = 5, BackColor = UiStyles.White, Padding = new Padding(14, 0, 14, 0), Margin = Padding.Empty };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Panel { Size = new Size(6, 6), BackColor = Color.FromArgb(201, 204, 208), Anchor = AnchorStyles.Left }, 0, 0);
        row.Controls.Add(new Label { Text = device.Alias, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Font = new Font(UiStyles.SecondaryFont, FontStyle.Bold), ForeColor = UiStyles.Ink, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
        row.Controls.Add(new Label { Text = $"{device.NodeId:x8} · last at {device.LastAddress}", AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        row.Controls.Add(new Label { Text = HumanAge(DateTimeOffset.UtcNow - device.LastSeen), AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Muted, Anchor = AnchorStyles.Left }, 3, 0);
        var remove = UiKit.PlainButton("Remove…");
        remove.Anchor = AnchorStyles.Right;
        remove.Click += (_, _) => RemoveClicked?.Invoke(device);
        row.Controls.Add(remove, 4, 0);
        row.Paint += (_, e) => { using var pen = new Pen(UiStyles.HairRule); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
        return row;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiStyles.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private static string Pluralize(int count, string singular, string plural) => count == 1 ? singular : plural;

    private static string CountWord(int count) => count switch
    {
        1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five",
        6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", _ => count.ToString(),
    };

    public static string HumanAge(TimeSpan age) => age < TimeSpan.FromSeconds(90) ? "just now"
        : age < TimeSpan.FromHours(1) ? $"not heard for {(int)age.TotalMinutes} minutes"
        : age < TimeSpan.FromDays(1) ? $"not heard for {(int)age.TotalHours} hours"
        : $"not heard for {(int)age.TotalDays} days";
}
