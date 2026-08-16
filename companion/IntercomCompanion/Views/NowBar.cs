namespace IntercomCompanion.Views;

/// <summary>The 76 px "now" band on the Talk screen: a coloured accent bar, a
/// kicker + title naming the current state, and a right-aligned detail line.</summary>
internal sealed class NowBar : UserControl
{
    private readonly Panel accent = new()
    {
        Width = 4, Anchor = AnchorStyles.Top | AnchorStyles.Bottom, BackColor = UiStyles.Green,
        Margin = new Padding(0, 16, 16, 16),
    };
    private readonly Label kicker = new()
    {
        Text = "IDLE", AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.Hint,
        ForeColor = UiStyles.Secondary, Margin = new Padding(0, 0, 0, 1),
    };
    private readonly Label title = new()
    {
        Text = "Idle", AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.NowTitle,
        ForeColor = UiStyles.Green, Margin = Padding.Empty,
    };
    private readonly Label detail = new()
    {
        AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.SecondaryFont,
        ForeColor = UiStyles.Secondary, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 0, 0, 0),
    };

    public NowBar()
    {
        Dock = DockStyle.Fill;
        BackColor = UiStyles.Surface;
        Padding = new Padding(20, 0, 20, 0);

        var textBlock = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        textBlock.Controls.Add(kicker);
        textBlock.Controls.Add(title);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(accent, 0, 0);
        root.Controls.Add(textBlock, 1, 0);
        root.Controls.Add(detail, 2, 0);
        Controls.Add(root);
    }

    public void Update(string kickerText, string titleText, string detailText, Color color, Color background)
    {
        kicker.Text = kickerText;
        title.Text = titleText;
        title.ForeColor = color;
        accent.BackColor = color;
        detail.Text = detailText;
        BackColor = background;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiStyles.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}
