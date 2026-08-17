namespace IntercomCompanion.Views.Settings;

/// <summary>"Firmware": the verified-package summary (never empty), the sequential
/// update queue, and the OTA status line.</summary>
internal sealed class FirmwarePage : SettingsPage
{
    public sealed record QueueRow(uint NodeId, string Alias, string Version, string State, Color StateColor, int Progress, string Action, string Detail, bool Removable);

    private readonly Label packageName = new() { Text = "No package selected", AutoSize = true, Font = UiStyles.PanelHeading, ForeColor = UiStyles.Ink, Margin = Padding.Empty };
    private readonly Label packageSummary = new() { Text = "Choose a signed .ota.json package to enable the queue.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(0, 3, 0, 0) };
    private readonly Button startQueue = UiKit.PrimaryButton("Start queue…");
    private readonly FlowLayoutPanel queueRows = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 818, BackColor = UiStyles.White, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(820, 0), Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(0, 12, 0, 0) };

    public FirmwarePage() : base(
        "Firmware",
        "Signed packages only. Updates go to the inactive slot; a device that does not report healthy rolls itself back.",
        820)
    {
        var package = UiKit.Card(820, new Padding(20, 18, 20, 18));
        var packageLayout = new TableLayoutPanel { Width = 780, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        packageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        packageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        packageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var info = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        info.Controls.Add(packageName);
        info.Controls.Add(packageSummary);
        var choose = UiKit.PlainButton("Choose another package…");
        choose.Anchor = AnchorStyles.Right;
        choose.Click += (_, _) => ChoosePackageClicked?.Invoke(this, EventArgs.Empty);
        packageLayout.Controls.Add(info, 0, 0);
        packageLayout.Controls.Add(choose, 1, 0);
        package.Controls.Add(packageLayout);
        Stack.Controls.Add(package);

        Stack.Controls.Add(BuildQueuePanel());
        Stack.Controls.Add(status);
        SetPackage(null, null);
    }

    public event EventHandler? ChoosePackageClicked;
    public event EventHandler? AddAllClicked;
    public event EventHandler? StartQueueClicked;
    public event Action<uint>? RowActionClicked;

    public void SetPackage(string? name, string? summary)
    {
        var loaded = name is not null;
        packageName.Text = loaded ? name! : "No package selected";
        packageSummary.Text = loaded ? summary! : "Choose a signed .ota.json package to enable the queue.";
        startQueue.Enabled = loaded;
    }

    public void SetStatus(string text, Color? color = null)
    {
        status.Text = text;
        status.ForeColor = color ?? UiStyles.Secondary;
    }

    public void SetQueue(IReadOnlyList<QueueRow> rows)
    {
        queueRows.SuspendLayout();
        queueRows.Controls.Clear();
        if (rows.Count == 0)
        {
            queueRows.Controls.Add(new Label
            {
                Text = "No devices queued. Add compatible active devices to update them sequentially.",
                AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary,
                Padding = new Padding(20, 16, 20, 16), Margin = Padding.Empty,
            });
            queueRows.ResumeLayout();
            return;
        }
        foreach (var row in rows) queueRows.Controls.Add(BuildRow(row));
        queueRows.ResumeLayout();
    }

    private Control BuildRow(QueueRow model)
    {
        var row = new TableLayoutPanel { Width = 818, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, BackColor = UiStyles.White, Padding = new Padding(20, 14, 20, 14), Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Paint += (_, e) => { using var pen = new Pen(UiStyles.HairRule); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };

        var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.Controls.Add(new Label { Text = model.Alias, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.BodyBold, ForeColor = UiStyles.Ink, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        head.Controls.Add(new Label { Text = model.Version, AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
        head.Controls.Add(new Label { Text = model.State, AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.SecondaryBold, ForeColor = model.StateColor, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        var action = UiKit.PlainButton(model.Action);
        action.Anchor = AnchorStyles.Right;
        action.Click += (_, _) => RowActionClicked?.Invoke(model.NodeId);
        head.Controls.Add(action, 3, 0);

        var progressTrack = new Panel { Dock = DockStyle.Fill, Height = 6, BackColor = UiStyles.ProgressTrack, Margin = new Padding(0, 10, 0, 0) };
        var progressFill = new Panel { Height = 6, BackColor = model.StateColor, Width = 0, Anchor = AnchorStyles.Top | AnchorStyles.Left };
        progressTrack.Controls.Add(progressFill);
        progressTrack.SizeChanged += (_, _) => progressFill.Width = (int)(progressTrack.Width * Math.Clamp(model.Progress, 0, 100) / 100.0);

        row.Controls.Add(head, 0, 0);
        row.Controls.Add(progressTrack, 0, 1);
        row.Controls.Add(new Label { Text = model.Detail, AutoSize = false, Width = 760, Height = 20, Font = UiStyles.Hint, ForeColor = UiStyles.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 0, 0) }, 0, 2);
        return row;
    }

    private Control BuildQueuePanel()
    {
        var panel = new TableLayoutPanel { Width = 820, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, BackColor = UiStyles.White, Margin = new Padding(0, 14, 0, 0), Padding = new Padding(1) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Paint += (_, e) => UiStyles.DrawBorder(e, panel);

        var header = new TableLayoutPanel { Width = 818, Height = 52, ColumnCount = 4, RowCount = 1, BackColor = UiStyles.White, Padding = new Padding(20, 0, 20, 0), Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var caption = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        caption.Controls.Add(new Label { Text = "Update queue", AutoSize = true, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Margin = Padding.Empty });
        caption.Controls.Add(new Label { Text = "sequential, one device at a time", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(10, 3, 0, 0) });
        var addAll = UiKit.PlainButton("Add all compatible");
        addAll.Anchor = AnchorStyles.Right;
        addAll.Margin = new Padding(0, 0, 8, 0);
        addAll.Click += (_, _) => AddAllClicked?.Invoke(this, EventArgs.Empty);
        startQueue.Anchor = AnchorStyles.Right;
        startQueue.Click += (_, _) => StartQueueClicked?.Invoke(this, EventArgs.Empty);
        header.Controls.Add(caption, 0, 0);
        header.Controls.Add(addAll, 2, 0);
        header.Controls.Add(startQueue, 3, 0);
        header.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };

        var footer = new Label { Text = "Keep the companion open. Windows may ask once to allow the temporary local firmware server on a private network.", AutoSize = true, Dock = DockStyle.Top, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Padding = new Padding(20, 14, 20, 14), Margin = Padding.Empty };
        footer.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };

        panel.Controls.Add(header, 0, 0);
        panel.Controls.Add(queueRows, 0, 1);
        panel.Controls.Add(footer, 0, 2);
        return panel;
    }
}
