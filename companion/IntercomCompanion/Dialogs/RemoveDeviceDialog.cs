namespace IntercomCompanion.Dialogs;

/// <summary>Confirmation for forgetting a device's local record. States plainly
/// that removal is local-only and does not touch the device or its group.</summary>
internal static class RemoveDeviceDialog
{
    public static bool ConfirmRemove(IWin32Window owner, string alias, string meshId)
    {
        var body =
            $"Removing forgets the device's alias, last-known volume and ring settings on this PC. It does not change the device itself, and it stays in group {meshId}.\n\n" +
            "If it powers back on, it reappears in Devices with the settings stored on the device.";
        return Show(owner, $"Remove {alias} from this companion?", body, "Remove");
    }

    public static bool ConfirmRemoveAll(IWin32Window owner, int count)
    {
        var body =
            $"Removing forgets the local records for {count} offline {(count == 1 ? "device" : "devices")} on this PC. It does not change the devices themselves, and it does not remove them from their group.\n\n" +
            "Any device that powers back on reappears in Devices with the settings stored on the device.";
        return Show(owner, $"Remove {count} offline {(count == 1 ? "device" : "devices")} from this companion?", body, "Remove all");
    }

    private static bool Show(IWin32Window owner, string title, string body, string confirmLabel)
    {
        using var dialog = new Form
        {
            Text = title,
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

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Width = 470, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = UiStyles.White, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Label { Text = title, AutoSize = true, MaximumSize = new Size(430, 0), Font = UiStyles.PanelHeading, ForeColor = UiStyles.Ink, Padding = new Padding(20, 16, 20, 16), Margin = Padding.Empty };
        header.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
        var text = new Label { Text = body, AutoSize = true, MaximumSize = new Size(430, 0), Font = UiStyles.BodyFont, ForeColor = UiStyles.Body, Padding = new Padding(20, 18, 20, 18), Margin = Padding.Empty };

        var footer = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Width = 470, Height = 60, BackColor = Color.FromArgb(247, 248, 249), Padding = new Padding(20, 0, 20, 0), Margin = Padding.Empty };
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var remove = UiKit.PrimaryButton(confirmLabel, UiStyles.Red);
        remove.Anchor = AnchorStyles.Left;
        remove.Margin = new Padding(0, 0, 8, 0);
        remove.DialogResult = DialogResult.OK;
        var cancel = UiKit.PlainButton("Cancel");
        cancel.Anchor = AnchorStyles.Left;
        cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(remove, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(text, 0, 1);
        root.Controls.Add(footer, 0, 2);
        dialog.Controls.Add(root);
        dialog.AcceptButton = remove;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}
