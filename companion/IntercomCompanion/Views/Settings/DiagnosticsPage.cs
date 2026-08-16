namespace IntercomCompanion.Views.Settings;

/// <summary>"Diagnostics": four live counters and the coloured discovery/session
/// log inside a dark wrapper whose scrollbar sits outside the visual padding.</summary>
internal sealed class DiagnosticsPage : SettingsPage
{
    private static readonly Color LogBack = Color.FromArgb(28, 31, 35);
    private static readonly Color LogBorder = Color.FromArgb(16, 18, 21);

    private readonly Label[] values = new Label[4];
    private readonly RichTextBox log = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BorderStyle = BorderStyle.None,
        BackColor = LogBack, ForeColor = UiStyles.Disabled, Font = UiStyles.Log,
        ScrollBars = RichTextBoxScrollBars.Vertical, Margin = Padding.Empty,
    };

    public DiagnosticsPage() : base(
        "Diagnostics",
        "Live counters and the discovery/session log. Copy this when reporting a fault.",
        860)
    {
        Stack.Controls.Add(BuildStatCards());

        var wrapper = new Panel { Width = 820, Height = 300, BackColor = LogBack, Padding = new Padding(16, 14, 0, 14), Margin = new Padding(0, 16, 0, 0) };
        wrapper.Paint += (_, e) => UiStyles.DrawBorder(e, wrapper, LogBorder);
        wrapper.Controls.Add(log);
        Stack.Controls.Add(wrapper);

        var copy = UiKit.PlainButton("Copy log");
        copy.Click += (_, _) => CopyLogClicked?.Invoke(this, EventArgs.Empty);
        var save = UiKit.PlainButton("Save log to file…");
        save.Click += (_, _) => SaveLogClicked?.Invoke(this, EventArgs.Empty);
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        copy.Margin = new Padding(0, 0, 8, 0);
        save.Margin = Padding.Empty;
        actions.Controls.Add(copy);
        actions.Controls.Add(save);
        Stack.Controls.Add(actions);
    }

    public event EventHandler? CopyLogClicked;
    public event EventHandler? SaveLogClicked;

    public void SetCounters(string udpAudio, string decoded, string plc, string outputBuffer)
    {
        values[0].Text = udpAudio;
        values[1].Text = decoded;
        values[2].Text = plc;
        values[3].Text = outputBuffer;
    }

    public void SetLogLines(IReadOnlyList<(string Text, Color Color)> lines)
    {
        log.Clear();
        foreach (var (text, color) in lines)
        {
            log.SelectionColor = color;
            log.AppendText(text + Environment.NewLine);
        }
        log.SelectionStart = 0;
        log.ScrollToCaret();
    }

    private Control BuildStatCards()
    {
        var grid = new TableLayoutPanel { Width = 820, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 18, 0, 0) };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        var labels = new[] { "UDP audio", "Decoded", "PLC frames", "Output buffer" };
        for (var i = 0; i < 4; i++)
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, BackColor = UiStyles.White, Padding = new Padding(16, 14, 16, 14), Margin = new Padding(i == 0 ? 0 : 6, 0, i == 3 ? 0 : 6, 0) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Paint += (_, e) => UiStyles.DrawBorder(e, card);
            card.Controls.Add(new Label { Text = labels[i].ToUpperInvariant(), AutoSize = true, Font = UiStyles.Hint, ForeColor = UiStyles.Muted, Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 0, 0);
            values[i] = new Label { Text = "—", AutoSize = true, Font = new Font("Segoe UI", 16.5f, FontStyle.Bold), ForeColor = UiStyles.Ink, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) };
            card.Controls.Add(values[i], 0, 1);
            grid.Controls.Add(card, i, 0);
        }
        return grid;
    }
}
