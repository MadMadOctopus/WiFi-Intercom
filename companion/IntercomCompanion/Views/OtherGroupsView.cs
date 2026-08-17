namespace IntercomCompanion.Views;

/// <summary>"Other groups on this network": groups announcing normally that this
/// companion has not joined. Hidden entirely when none are present.</summary>
internal sealed class OtherGroupsView : UserControl
{
    public sealed record Row(string Code, string Label, int Count, string Members, string Note, Color NoteColor);

    private readonly TableLayoutPanel body = new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1, BackColor = UiStyles.White, Margin = Padding.Empty, Padding = Padding.Empty,
    };
    private string layoutKey = "";

    public OtherGroupsView()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = UiStyles.White;
        Margin = new Padding(0, 18, 12, 0);
        Visible = false;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(body);
    }

    public event Action<string>? JoinClicked;

    public void Update(IReadOnlyList<Row> rows)
    {
        Visible = rows.Count > 0;
        var key = string.Join(";", rows.Select(row => $"{row.Code}|{row.Count}|{row.Members}|{row.Note}"));
        if (key == layoutKey) return;
        layoutKey = key;

        body.SuspendLayout();
        body.Controls.Clear();
        body.RowStyles.Clear();
        body.RowCount = 0;
        if (rows.Count == 0) { body.ResumeLayout(); return; }

        AddRow(BuildHeader());
        foreach (var row in rows) AddRow(BuildRow(row));
        body.ResumeLayout();
    }

    private void AddRow(Control control)
    {
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(control, 0, body.RowCount++);
    }

    private static Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 2, RowCount = 1, BackColor = UiStyles.White, Padding = new Padding(14, 0, 14, 0), Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "Other groups on this network", AutoSize = true, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 0, 0);
        header.Controls.Add(new Label { Text = "Announcing normally, but this companion is not a member — join a group to hear it and talk to it", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left }, 1, 0);
        header.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
        return header;
    }

    private Control BuildRow(Row model)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 5, RowCount = 1, BackColor = UiStyles.White, Padding = new Padding(14, 10, 14, 10), Margin = Padding.Empty };
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Panel { Size = new Size(6, 6), BackColor = UiStyles.Disabled, Anchor = AnchorStyles.Left }, 0, 0);
        row.Controls.Add(new Label { Text = model.Label, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Font = new Font(UiStyles.SecondaryFont, FontStyle.Bold), ForeColor = UiStyles.Ink, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
        row.Controls.Add(new Label { Text = model.Count == 1 ? "1 device" : $"{model.Count} devices", AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);

        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detail.Controls.Add(new Label { Text = model.Members, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Height = 18, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Body, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
        detail.Controls.Add(new Label { Text = model.Note, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Height = 16, Font = UiStyles.Hint, ForeColor = model.NoteColor, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 0, 0) }, 0, 1);
        row.Controls.Add(detail, 3, 0);

        var join = UiKit.PlainButton("Join group…");
        join.Anchor = AnchorStyles.Right;
        join.Click += (_, _) => JoinClicked?.Invoke(model.Code);
        row.Controls.Add(join, 4, 0);
        row.Paint += (_, e) => { using var pen = new Pen(UiStyles.HairRule); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
        return row;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiStyles.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
