namespace IntercomCompanion.Views;

/// <summary>The window shell: identity strip, now bar, body host and status bar
/// in a four-row table. Page switching toggles Visible only; nothing is ever
/// added to or removed from a container after construction.</summary>
internal sealed class ShellView : UserControl
{
    private readonly TableLayoutPanel root = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = UiStyles.Surface, Margin = Padding.Empty };
    private readonly NowBar nowBar;
    private readonly TalkView talkView;
    private readonly SettingsView settingsView;

    public ShellView(IdentityStrip identity, NowBar nowBar, TalkView talk, SettingsView settings, AppStatusBar status)
    {
        this.nowBar = nowBar;
        talkView = talk;
        settingsView = settings;
        Dock = DockStyle.Fill;
        BackColor = UiStyles.Surface;

        var bodyHost = new Panel { Dock = DockStyle.Fill, BackColor = UiStyles.Surface };
        bodyHost.Controls.Add(settings);
        bodyHost.Controls.Add(talk);

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.Controls.Add(identity, 0, 0);
        root.Controls.Add(nowBar, 0, 1);
        root.Controls.Add(bodyHost, 0, 2);
        root.Controls.Add(status, 0, 3);
        Controls.Add(root);

        ShowTalk();
    }

    public void ShowTalk()
    {
        settingsView.Visible = false;
        talkView.Visible = true;
        nowBar.Visible = true;
        root.RowStyles[1].Height = 76;
    }

    public void ShowSettings()
    {
        talkView.Visible = false;
        settingsView.Visible = true;
        nowBar.Visible = false;
        root.RowStyles[1].Height = 0;
    }
}

/// <summary>The 30 px status bar: a filling left message and a right-aligned hint.</summary>
internal sealed class AppStatusBar : UserControl
{
    private readonly Label left = new() { AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
    private readonly Label right = new() { Text = "Running in the notification area · Ctrl+Alt+B to broadcast", AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Right };

    public AppStatusBar()
    {
        Dock = DockStyle.Fill;
        BackColor = UiStyles.Chrome;
        Padding = new Padding(16, 0, 16, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(left, 0, 0);
        layout.Controls.Add(right, 1, 0);
        Controls.Add(layout);
    }

    public void SetMessage(string text) => left.Text = text;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiStyles.Border);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}
