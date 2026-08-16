using IntercomCompanion.Views.Settings;

namespace IntercomCompanion.Views;

/// <summary>The Settings screen: a 236 px nav column and a content host that owns
/// the five pages, switching them by Visible only.</summary>
internal sealed class SettingsView : UserControl
{
    private static readonly (string Key, string Label)[] Nav =
    [
        ("USB", "Set up a device (USB)"),
        ("Group", "Group and device IDs"),
        ("Firmware", "Firmware"),
        ("Identity", "Identity, audio, shortcuts"),
        ("Diagnostics", "Diagnostics"),
    ];

    private readonly Dictionary<string, Label> navItems = [];
    private readonly Dictionary<string, UserControl> pages = [];
    private string active = "USB";

    public SettingsView()
    {
        Dock = DockStyle.Fill;
        BackColor = UiStyles.Surface;

        Usb = new UsbPage();
        Group = new GroupIdsPage();
        Firmware = new FirmwarePage();
        Identity = new IdentityPage();
        Diagnostics = new DiagnosticsPage();
        pages["USB"] = Usb;
        pages["Group"] = Group;
        pages["Firmware"] = Firmware;
        pages["Identity"] = Identity;
        pages["Diagnostics"] = Diagnostics;

        var contentHost = new Panel { Dock = DockStyle.Fill, BackColor = UiStyles.Surface };
        foreach (var page in pages.Values) contentHost.Controls.Add(page);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiStyles.Surface, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildNav(), 0, 0);
        root.Controls.Add(contentHost, 1, 0);
        Controls.Add(root);

        Show("USB");
    }

    public UsbPage Usb { get; }
    public GroupIdsPage Group { get; }
    public FirmwarePage Firmware { get; }
    public IdentityPage Identity { get; }
    public DiagnosticsPage Diagnostics { get; }

    public event Action<string>? PageShown;
    public event Action? BackToTalkClicked;

    public void Show(string page)
    {
        active = page;
        foreach (var (key, control) in pages) control.Visible = key == page;
        foreach (var (key, item) in navItems)
        {
            var selected = key == page;
            item.BackColor = selected ? UiStyles.White : Color.Transparent;
            item.ForeColor = selected ? UiStyles.Ink : UiStyles.Body;
            item.Font = new Font(UiStyles.BodyFont, selected ? FontStyle.Bold : FontStyle.Regular);
            item.Invalidate();
        }
        PageShown?.Invoke(page);
    }

    private Control BuildNav()
    {
        var nav = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = Nav.Length + 5, BackColor = UiStyles.Chrome, Padding = new Padding(0, 12, 0, 12), Margin = Padding.Empty };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nav.Paint += (_, e) => { using var pen = new Pen(UiStyles.Border); e.Graphics.DrawLine(pen, nav.Width - 1, 0, nav.Width - 1, nav.Height); };

        var row = 0;
        // A permanent navigation affordance — not a nav item: no accent bar and no
        // selected state (adjustments/01-back-to-talk.md).
        nav.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        nav.Controls.Add(BuildBackRow(), 0, row++);
        nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nav.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Top, BackColor = UiStyles.Border, Margin = new Padding(0, 10, 0, 12) }, 0, row++);

        nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nav.Controls.Add(new Label { Text = "SETTINGS", AutoSize = false, Height = 26, Dock = DockStyle.Top, Font = UiStyles.Hint, ForeColor = UiStyles.Muted, Padding = new Padding(18, 0, 18, 10), TextAlign = ContentAlignment.BottomLeft, Margin = Padding.Empty }, 0, row++);

        for (var i = 0; i < Nav.Length; i++)
        {
            var (key, label) = Nav[i];
            var item = new Label
            {
                Text = label, AutoSize = false, Dock = DockStyle.Top, Height = 40, Font = UiStyles.BodyFont, ForeColor = UiStyles.Body,
                Padding = new Padding(18, 0, 18, 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = false, Cursor = Cursors.Hand, Margin = Padding.Empty,
            };
            item.Click += (_, _) => Show(key);
            item.Paint += (_, e) =>
            {
                if (key != active) return;
                using var brush = new SolidBrush(UiStyles.Blue);
                e.Graphics.FillRectangle(brush, 0, 0, 3, item.Height);
            };
            navItems[key] = item;
            nav.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            nav.Controls.Add(item, 0, row++);
        }

        nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nav.Controls.Add(new Label
        {
            Text = "Settings are rare. Nothing here blocks discovery, talking or the device list.",
            AutoSize = true, MaximumSize = new Size(200, 0), Dock = DockStyle.Top, Font = UiStyles.Hint, ForeColor = UiStyles.Muted,
            Padding = new Padding(18, 0, 18, 0), Margin = new Padding(0, 14, 0, 0),
        }, 0, row++);

        nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        nav.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = UiStyles.Chrome, Margin = Padding.Empty }, 0, row);
        return nav;
    }

    private Panel BuildBackRow()
    {
        var backRow = new Panel { Dock = DockStyle.Top, Height = 38, Cursor = Cursors.Hand, BackColor = UiStyles.Chrome, Margin = Padding.Empty };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(15, 0, 18, 0) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var glyph = new Label { Text = "←", AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.CardAlias, ForeColor = UiStyles.Blue, Cursor = Cursors.Hand, Margin = Padding.Empty };
        var text = new Label { Text = "Back to Talk", AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.BodyFont, ForeColor = UiStyles.Blue, Cursor = Cursors.Hand, Margin = new Padding(6, 0, 0, 0) };
        var hint = new Label { Text = "Esc", AutoSize = true, Anchor = AnchorStyles.Right, Font = UiStyles.Hint, ForeColor = UiStyles.Muted, Cursor = Cursors.Hand };
        layout.Controls.Add(glyph, 0, 0);
        layout.Controls.Add(text, 1, 0);
        layout.Controls.Add(hint, 3, 0);
        backRow.Controls.Add(layout);

        // Keep the highlight while the pointer crosses the child labels.
        void Enter(object? _, EventArgs __) => backRow.BackColor = UiStyles.NavHover;
        void Leave(object? _, EventArgs __) => backRow.BackColor = UiStyles.Chrome;
        void Click(object? _, EventArgs __) { backRow.BackColor = UiStyles.Chrome; BackToTalkClicked?.Invoke(); }
        foreach (Control control in new Control[] { backRow, layout, glyph, text, hint })
        {
            control.MouseEnter += Enter;
            control.MouseLeave += Leave;
            control.Click += Click;
        }
        return backRow;
    }
}
