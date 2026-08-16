namespace IntercomCompanion.Dialogs;

/// <summary>Confirmation before an OTA queue runs: package identity, the four
/// consequences, the device order, and an acknowledgement that gates Start
/// update.</summary>
internal static class ConfirmFirmwareUpdateDialog
{
    public sealed record Target(string Alias, uint NodeId, string FromVersion, string ToVersion);

    public static bool Confirm(IWin32Window owner, string version, string packageSummary, IReadOnlyList<Target> targets)
    {
        using var dialog = new Form
        {
            Text = "Confirm firmware update",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = UiStyles.White,
            Font = new Font("Segoe UI", 9f),
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Width = 540, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = UiStyles.White, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 540, BackColor = UiStyles.White, Padding = new Padding(20, 16, 20, 16), Margin = Padding.Empty };
        header.Controls.Add(new Label { Text = $"Update {targets.Count} {(targets.Count == 1 ? "device" : "devices")} to {version}?", AutoSize = true, Font = UiStyles.PanelHeading, ForeColor = UiStyles.Ink, Margin = Padding.Empty });
        header.Controls.Add(new Label { Text = packageSummary, AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(0, 4, 0, 0) });
        header.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };

        var body = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 540, BackColor = UiStyles.White, Padding = new Padding(20, 18, 20, 18), Margin = Padding.Empty };

        var warning = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 500, BackColor = UiStyles.AmberTint, Padding = new Padding(14, 12, 14, 12), Margin = Padding.Empty };
        warning.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(232, 217, 168));
            e.Graphics.DrawRectangle(pen, 0, 0, warning.Width - 1, warning.Height - 1);
            using var bar = new SolidBrush(UiStyles.Amber);
            e.Graphics.FillRectangle(bar, 0, 0, 4, warning.Height);
        };
        foreach (var consequence in new[]
        {
            "· Devices update one at a time and reboot; each is unavailable for about a minute.",
            "· The companion must stay open and awake for the whole queue.",
            "· Windows may ask once to allow the temporary local firmware server on a private network.",
            "· A device that does not report healthy rolls back to its current firmware by itself.",
        })
            warning.Controls.Add(new Label { Text = consequence, AutoSize = true, MaximumSize = new Size(468, 0), Font = UiStyles.BodyFont, ForeColor = Color.FromArgb(107, 83, 0), Margin = new Padding(0, 0, 0, 4) });
        body.Controls.Add(warning);

        body.Controls.Add(new Label { Text = "In order:", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(0, 16, 0, 0) });
        foreach (var target in targets)
        {
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            row.Controls.Add(new Label { Text = target.Alias, AutoSize = true, Font = new Font(UiStyles.BodyFont, FontStyle.Bold), ForeColor = UiStyles.Ink, Margin = Padding.Empty });
            row.Controls.Add(new Label { Text = $"{target.NodeId:x8} · {target.FromVersion} → {target.ToVersion}", AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Secondary, Margin = new Padding(8, 0, 0, 0) });
            body.Controls.Add(row);
        }

        var acknowledge = new CheckBox { Text = "I understand the devices will reboot and be unavailable", AutoSize = true, Font = UiStyles.BodyFont, Margin = new Padding(0, 16, 0, 0) };
        body.Controls.Add(acknowledge);

        var footer = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 1, Width = 540, Height = 60, BackColor = Color.FromArgb(247, 248, 249), Padding = new Padding(20, 0, 20, 0), Margin = Padding.Empty };
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var start = UiKit.PrimaryButton("Start update");
        start.Enabled = false;
        start.Anchor = AnchorStyles.Left;
        start.Margin = new Padding(0, 0, 8, 0);
        start.DialogResult = DialogResult.OK;
        acknowledge.CheckedChanged += (_, _) => start.Enabled = acknowledge.Checked;
        var cancel = UiKit.PlainButton("Cancel");
        cancel.Anchor = AnchorStyles.Left;
        cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(start, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(new Label { Text = "Local PTT is disabled while a queue runs.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Right }, 2, 0);
        footer.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);
        root.Controls.Add(footer, 0, 2);
        dialog.Controls.Add(root);
        dialog.AcceptButton = start;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK && acknowledge.Checked;
    }
}
