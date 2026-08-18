namespace IntercomCompanion.Views;

/// <summary>The Talk screen: a filling left column (grid + known devices +
/// footnote) and a fixed 336 px right column (broadcast / reply + activity).</summary>
internal sealed class TalkView : UserControl
{
    private readonly Label lastSender = new() { Text = "Last sender: none", AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left };
    private readonly Label broadcastScope = new() { Text = "Everyone in group MESH", AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left };

    public TalkView()
    {
        Dock = DockStyle.Fill;
        BackColor = UiStyles.Surface;

        Grid = new DeviceGridView { Margin = new Padding(0, 0, 12, 0) };
        OtherGroups = new OtherGroupsView();
        KnownDevices = new KnownDevicesView { Margin = new Padding(0, 16, 12, 0) };
        Activity = new ActivityPanel();

        var footnote = new Label
        {
            Text = "Up to 16 devices per group. Cards are one row high and the grid scrolls, so about nine are visible at 1280 × 820 and all sixteen at full screen; ring, buttons and orientation live in each device's Configure dialog. Silence is a soft mute on the device: it stops that device playing received audio while it stays in discovery and floor control. A device whose physical mute slider is on reports that state and its Silence control is unavailable.",
            AutoSize = true, MaximumSize = new Size(900, 0), Font = UiStyles.Hint, ForeColor = UiStyles.Muted, Margin = new Padding(0, 16, 12, 0),
        };

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = UiStyles.Surface, Margin = Padding.Empty };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(Grid, 0, 0);
        left.Controls.Add(OtherGroups, 0, 1);
        left.Controls.Add(KnownDevices, 0, 2);
        left.Controls.Add(footnote, 0, 3);

        var right = BuildRightColumn();

        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiStyles.Surface, Padding = new Padding(20, 18, 20, 18), Margin = Padding.Empty };
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 336));
        page.Controls.Add(left, 0, 0);
        page.Controls.Add(right, 1, 0);
        Controls.Add(page);
    }

    public DeviceGridView Grid { get; }
    public OtherGroupsView OtherGroups { get; }
    public KnownDevicesView KnownDevices { get; }
    public ActivityPanel Activity { get; }
    public Button Broadcast { get; private set; } = null!;
    public Button Reply { get; private set; } = null!;

    public void SetBroadcastScope(string groupLabel) => broadcastScope.Text = $"Everyone in {groupLabel}";
    public void SetLastSender(string text) => lastSender.Text = text;

    private Control BuildRightColumn()
    {
        Broadcast = PttButton("Hold to broadcast", UiStyles.Green, UiStyles.Broadcast);
        Reply = PttButton("Hold to reply", UiStyles.Blue, UiStyles.CardAlias);

        var ptt = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 5, BackColor = UiStyles.White, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 12), Height = 210 };
        ptt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ptt.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ptt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ptt.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        ptt.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        ptt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ptt.Controls.Add(Broadcast, 0, 0);
        ptt.Controls.Add(HintRow(broadcastScope, "Space, or Ctrl+Alt+B anywhere"), 0, 1);
        ptt.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = UiStyles.White, Margin = Padding.Empty }, 0, 2);
        ptt.Controls.Add(Reply, 0, 3);
        ptt.Controls.Add(HintRow(lastSender, "Ctrl+Alt+R"), 0, 4);
        ptt.Paint += (_, e) => { using var pen = new Pen(UiStyles.Border); e.Graphics.DrawRectangle(pen, 0, 0, ptt.Width - 1, ptt.Height - 1); };

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = UiStyles.Surface, Margin = new Padding(0, 0, 0, 0), Padding = Padding.Empty };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(ptt, 0, 0);
        right.Controls.Add(Activity, 0, 1);
        return right;
    }

    private static TableLayoutPanel HintRow(Label left, string rightText)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 8, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(left, 0, 0);
        row.Controls.Add(new Label { Text = rightText, AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Right }, 1, 0);
        return row;
    }

    private static Button PttButton(string text, Color color, Font font)
    {
        var button = new Button
        {
            Text = text, Dock = DockStyle.Fill, AutoSize = false, FlatStyle = FlatStyle.Flat,
            BackColor = color, ForeColor = UiStyles.White, Font = font, Margin = Padding.Empty,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = color;
        button.FlatAppearance.MouseOverBackColor = color;
        button.FlatAppearance.MouseDownBackColor = color;
        return button;
    }
}
