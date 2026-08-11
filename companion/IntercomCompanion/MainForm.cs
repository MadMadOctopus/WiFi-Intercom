namespace IntercomCompanion;

/// <summary>
/// Deliberately thin WinForms surface. Networking, protocol, state and audio
/// will stay outside this class so no network or audio callback can block UI.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly Label statusLabel = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 18, FontStyle.Bold),
        ForeColor = Color.DimGray,
        Text = "Starting…"
    };

    public MainForm()
    {
        Text = "Wi-Fi Intercom Companion";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);
        Size = new Size(960, 650);
        Font = new Font("Segoe UI", 9);

        var title = new Label
        {
            Text = "Wi-Fi Intercom Companion",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            AutoSize = true,
        };
        var network = new Label
        {
            Text = "Mesh discovery: starting…",
            AutoSize = true,
            ForeColor = Color.DimGray,
        };

        var broadcast = CreatePttButton("Hold to broadcast", Color.FromArgb(46, 125, 50));
        var reply = CreatePttButton("Hold to reply", Color.FromArgb(21, 101, 192));
        var selected = CreatePttButton("Hold to selected device", Color.FromArgb(106, 27, 154));
        broadcast.Enabled = reply.Enabled = selected.Enabled = false;

        var pttPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 8),
        };
        pttPanel.Controls.AddRange([broadcast, reply, selected]);

        var devices = new ListView
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            View = View.Details,
        };
        devices.Columns.Add("Alias", 150);
        devices.Columns.Add("Device ID", 110);
        devices.Columns.Add("Address", 135);
        devices.Columns.Add("Last seen", 90);

        var deviceGroup = new GroupBox { Text = "Active devices", Dock = DockStyle.Fill };
        deviceGroup.Controls.Add(devices);

        var config = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
        };
        config.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddConfigField(config, "Alias", new TextBox { Enabled = false });
        AddConfigField(config, "Volume", new NumericUpDown { Enabled = false, Minimum = 64, Maximum = 1024 });
        AddConfigField(config, "Ring brightness", new NumericUpDown { Enabled = false, Minimum = 0, Maximum = 255 });
        AddConfigField(config, "Buttons swapped", new CheckBox { Enabled = false });
        AddConfigField(config, "Ring orientation", new ComboBox { Enabled = false, DropDownStyle = ComboBoxStyle.DropDownList });
        var getConfig = new Button { Text = "Get configuration", Enabled = false, AutoSize = true };
        var applyConfig = new Button { Text = "Apply configuration", Enabled = false, AutoSize = true };
        var configActions = new FlowLayoutPanel { AutoSize = true };
        configActions.Controls.AddRange([getConfig, applyConfig]);
        config.SetColumnSpan(configActions, 2);
        config.Controls.Add(configActions, 0, config.RowCount++);

        var configGroup = new GroupBox { Text = "Selected device configuration", Dock = DockStyle.Fill };
        configGroup.Controls.Add(config);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 0, 12, 12) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        body.Controls.Add(deviceGroup, 0, 0);
        body.Controls.Add(configGroup, 1, 0);

        var top = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 12, 12, 0),
        };
        top.Controls.AddRange([title, network, statusLabel, pttPanel]);

        Controls.Add(body);
        Controls.Add(top);
    }

    private static Button CreatePttButton(string text, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = color,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 10, FontStyle.Bold),
        Margin = new Padding(0, 0, 8, 0),
        Padding = new Padding(12, 8, 12, 8),
        UseVisualStyleBackColor = false,
    };

    private static void AddConfigField(TableLayoutPanel panel, string label, Control field)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, panel.RowCount);
        field.Dock = DockStyle.Fill;
        panel.Controls.Add(field, 1, panel.RowCount++);
    }
}
