namespace IntercomCompanion.Views;

/// <summary>The Activity panel in the Talk right column: a header with the
/// recordings link, a scrolling list of recent sessions, and a Diagnostics…
/// footer inside the same panel.</summary>
internal sealed class ActivityPanel : UserControl
{
    private readonly Panel rows = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiStyles.White, Margin = Padding.Empty, Padding = Padding.Empty };

    public ActivityPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = UiStyles.White;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = UiStyles.White, Padding = new Padding(16, 0, 16, 0) };
        var diagnostics = new LinkLabel { Text = "Diagnostics…", AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.SecondaryFont, LinkColor = UiStyles.Blue, ActiveLinkColor = UiStyles.DeepBlue };
        diagnostics.LinkClicked += (_, _) => DiagnosticsClicked?.Invoke(this, EventArgs.Empty);
        var footerHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        footerHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footerHost.Controls.Add(diagnostics, 0, 0);
        footer.Controls.Add(footerHost);
        footer.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };

        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 2, RowCount = 1, BackColor = UiStyles.White, Padding = new Padding(16, 0, 14, 0), Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Activity", AutoSize = true, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Anchor = AnchorStyles.Left }, 0, 0);
        var recordings = new LinkLabel { Text = "Open recordings folder", AutoSize = true, Anchor = AnchorStyles.Right, Font = UiStyles.SecondaryFont, LinkColor = UiStyles.Blue, ActiveLinkColor = UiStyles.DeepBlue };
        recordings.LinkClicked += (_, _) => OpenRecordingsClicked?.Invoke(this, EventArgs.Empty);
        header.Controls.Add(recordings, 1, 0);
        header.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };

        Controls.Add(rows);
        Controls.Add(footer);
        Controls.Add(header);
    }

    public event EventHandler? OpenRecordingsClicked;
    public event EventHandler? DiagnosticsClicked;

    public void Add(string text)
    {
        var color = text.Contains("failed", StringComparison.OrdinalIgnoreCase) || text.Contains("stopped", StringComparison.OrdinalIgnoreCase) ? UiStyles.Red
            : text.Contains("busy", StringComparison.OrdinalIgnoreCase) || text.Contains("claim", StringComparison.OrdinalIgnoreCase) || text.Contains("dropped", StringComparison.OrdinalIgnoreCase) ? UiStyles.Amber
            : text.Contains("receiv", StringComparison.OrdinalIgnoreCase) || text.Contains("direct", StringComparison.OrdinalIgnoreCase) ? UiStyles.Blue
            : UiStyles.Green;

        var row = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 3, RowCount = 1, BackColor = UiStyles.White, Padding = new Padding(16, 0, 16, 0), Margin = Padding.Empty };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = DateTime.Now.ToString("HH:mm"), AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Muted, Anchor = AnchorStyles.Left }, 0, 0);
        row.Controls.Add(new Panel { Size = new Size(8, 8), BackColor = color, Anchor = AnchorStyles.Left }, 1, 0);
        row.Controls.Add(new Label { Text = text, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Body, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        row.Paint += (_, e) => { using var pen = new Pen(UiStyles.HairRule); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };

        rows.Controls.Add(row);
        rows.Controls.SetChildIndex(row, 0);
        while (rows.Controls.Count > 40)
        {
            var old = rows.Controls[^1];
            rows.Controls.Remove(old);
            old.Dispose();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiStyles.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
