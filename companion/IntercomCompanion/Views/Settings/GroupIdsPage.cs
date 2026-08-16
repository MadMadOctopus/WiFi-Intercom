using IntercomCompanion.Core;

namespace IntercomCompanion.Views.Settings;

/// <summary>"Group and device IDs": the current-group summary, the red danger
/// block whose Change button is gated on a typed confirmation, and the device-ID
/// note.</summary>
internal sealed class GroupIdsPage : SettingsPage
{
    private readonly Label currentGroup = new() { AutoSize = true, Font = new Font(UiStyles.BodyFont, FontStyle.Bold), ForeColor = UiStyles.Ink, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
    private readonly TextBox newGroup = new() { MaxLength = 4, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox confirm = new() { MaxLength = 4, CharacterCasing = CharacterCasing.Upper };
    private readonly CheckBox applyCompanion = new() { Text = "This companion", Checked = true, AutoSize = true, BackColor = UiStyles.RedTint, Font = UiStyles.BodyFont };
    private readonly CheckBox applyActive = new() { Text = "All active devices, one at a time (each reboots)", AutoSize = true, BackColor = UiStyles.RedTint, Font = UiStyles.BodyFont };
    private readonly DisabledTintButton changeButton = new() { Text = "Change group ID", Enabled = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
    private readonly Label confirmField = new();
    private readonly Label firstConsequence = new() { AutoSize = true, MaximumSize = new Size(716, 0), Font = UiStyles.BodyFont, ForeColor = UiStyles.Body, Margin = new Padding(0, 0, 0, 6) };

    private string currentMeshId = "MESH";

    public GroupIdsPage() : base(
        "Group and device IDs",
        "A group is an intercom channel, not a radio network. Every device and companion that should hear each other must carry the same group ID on the same 2.4 GHz LAN. A group holds up to 16 devices.",
        760)
    {
        var summary = UiKit.Card(760, new Padding(20, 14, 20, 14));
        var summaryGrid = UiKit.FormGrid(170);
        UiKit.AddField(summaryGrid, "Current group", currentGroup);
        UiKit.AddField(summaryGrid, "This companion's ID", CompanionIdLabel);
        summary.Controls.Add(summaryGrid);
        Stack.Controls.Add(summary);

        Stack.Controls.Add(BuildDangerBlock());

        var ids = UiKit.Card(760, new Padding(20, 14, 20, 16), 16);
        var idStack = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 718, Margin = Padding.Empty };
        idStack.Controls.Add(new Label { Text = "Device IDs", AutoSize = true, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Margin = Padding.Empty });
        idStack.Controls.Add(new Label { Text = "A device ID identifies one unit inside the group and must be unique. Change it from that device's Configure dialog; the companion refuses an ID already held by another device it can see.", AutoSize = true, MaximumSize = new Size(710, 0), Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(0, 6, 0, 0) });
        ids.Controls.Add(idStack);
        Stack.Controls.Add(ids);

        UiStyles.StyleInput(newGroup, 140);
        UiStyles.StyleInput(confirm, 140);
        newGroup.KeyPress += RejectNonAlphanumeric;
        newGroup.TextChanged += (_, _) => UpdateGate();
        confirm.TextChanged += (_, _) => UpdateGate();
        applyCompanion.CheckedChanged += (_, _) => UpdateGate();
        applyActive.CheckedChanged += (_, _) => UpdateGate();
        changeButton.Click += (_, _) => { if (changeButton.Enabled) ApplyClicked?.Invoke(this, EventArgs.Empty); };
    }

    private Label CompanionIdLabel { get; } = new() { AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };

    public event EventHandler? ApplyClicked;

    public string NewGroupId => newGroup.Text.Trim();
    public bool ApplyToCompanion => applyCompanion.Checked;
    public bool ApplyToActiveDevices => applyActive.Checked;

    public void SetCompanionId(uint nodeId) => CompanionIdLabel.Text = $"{nodeId:x8} · generated once, unique on this PC";

    public void SetContext(string meshId, int activeDevices)
    {
        currentMeshId = meshId;
        currentGroup.Text = $"{meshId} · {activeDevices} {(activeDevices == 1 ? "device" : "devices")}, 1 companion";
        confirmField.Text = $"Type {meshId} to confirm";
        firstConsequence.Text = $"· Devices still on {meshId} disappear from this companion and can no longer hear it.";
        applyActive.Text = $"All {activeDevices} active {(activeDevices == 1 ? "device" : "devices")}, one at a time (each reboots)";
        UpdateGate();
    }

    public void Reset()
    {
        newGroup.Clear();
        confirm.Clear();
        UpdateGate();
    }

    private void UpdateGate()
    {
        var enabled = Protocol.IsValidMeshId(newGroup.Text) && confirm.Text == currentMeshId && (applyCompanion.Checked || applyActive.Checked);
        changeButton.Enabled = enabled;
        changeButton.BackColor = enabled ? UiStyles.Red : Color.FromArgb(208, 138, 138);
        changeButton.ForeColor = UiStyles.White;
        changeButton.Font = new Font(UiStyles.SecondaryFont, FontStyle.Bold);
        changeButton.Padding = new Padding(14, 7, 14, 7);
        changeButton.FlatAppearance.BorderColor = changeButton.BackColor;
    }

    private static void RejectNonAlphanumeric(object? sender, KeyPressEventArgs e)
    {
        if (char.IsControl(e.KeyChar)) return;
        var upper = char.ToUpperInvariant(e.KeyChar);
        if (!(upper is >= 'A' and <= 'Z' || upper is >= '0' and <= '9')) e.Handled = true;
    }

    private Control BuildDangerBlock()
    {
        var block = new TableLayoutPanel { Width = 760, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, BackColor = UiStyles.RedTint, Margin = new Padding(0, 18, 0, 0) };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        block.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        block.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        block.Paint += (_, e) =>
        {
            using var pen = new Pen(UiStyles.Red);
            e.Graphics.DrawRectangle(pen, 0, 0, block.Width - 1, block.Height - 1);
            using var bar = new SolidBrush(UiStyles.Red);
            e.Graphics.FillRectangle(bar, 0, 0, 4, block.Height);
        };

        var header = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 758, Padding = new Padding(18, 14, 18, 14), Margin = Padding.Empty, BackColor = UiStyles.RedTint };
        header.Controls.Add(new Label { Text = "Change the group ID", AutoSize = true, Font = UiStyles.PanelHeading, ForeColor = UiStyles.DarkRed, Margin = Padding.Empty });
        header.Controls.Add(new Label { Text = "This breaks the intercom until every node carries the new ID.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = Color.FromArgb(122, 32, 32), Margin = new Padding(0, 4, 0, 0) });
        header.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(242, 214, 214)); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };

        var body = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 758, Padding = new Padding(18, 16, 18, 16), Margin = Padding.Empty, BackColor = UiStyles.RedTint };
        firstConsequence.Text = "· Devices still on MESH disappear from this companion and can no longer hear it.";
        body.Controls.Add(firstConsequence);
        foreach (var consequence in new[]
        {
            "· Each device must be changed separately, over USB or with a configuration write, and each one reboots.",
            "· A device you cannot reach right now keeps the old group until you get to it physically.",
            "· Two groups on the same LAN never mix, so a half-finished change leaves two isolated intercoms.",
        })
            body.Controls.Add(new Label { Text = consequence, AutoSize = true, MaximumSize = new Size(716, 0), Font = UiStyles.BodyFont, ForeColor = UiStyles.Body, Margin = new Padding(0, 0, 0, 6) });

        var fields = UiKit.FormGrid(170);
        fields.Margin = new Padding(0, 12, 0, 0);
        fields.BackColor = UiStyles.RedTint;
        UiKit.AddField(fields, "New group ID", newGroup, UiKit.Hint("exactly 4 characters, A–Z and 0–9"));
        confirmField.AutoSize = true;
        confirmField.Font = UiStyles.BodyFont;
        confirmField.ForeColor = UiStyles.Ink;
        confirmField.Anchor = AnchorStyles.Left;
        confirmField.Padding = new Padding(0, 7, 16, 7);
        confirmField.Margin = Padding.Empty;
        confirmField.Text = "Type MESH to confirm";
        var confirmRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(confirmField, 0, confirmRow);
        var confirmPanel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        confirm.Margin = new Padding(0, 4, 8, 4);
        confirmPanel.Controls.Add(confirm);
        fields.Controls.Add(confirmPanel, 1, confirmRow);

        var choices = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, BackColor = UiStyles.RedTint };
        applyCompanion.Margin = new Padding(0, 4, 0, 4);
        applyActive.Margin = new Padding(0, 4, 0, 4);
        choices.Controls.Add(applyCompanion);
        choices.Controls.Add(applyActive);
        UiKit.AddField(fields, "Apply to", choices);

        var action = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 12, 0, 0), BackColor = UiStyles.RedTint };
        changeButton.Margin = Padding.Empty;
        action.Controls.Add(changeButton);
        action.Controls.Add(new Label { Text = "Enabled once the confirmation matches.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = Color.FromArgb(122, 32, 32), Margin = new Padding(12, 9, 0, 0) });

        body.Controls.Add(fields);
        body.Controls.Add(action);
        block.Controls.Add(header, 0, 0);
        block.Controls.Add(body, 0, 1);
        UpdateGate();
        return block;
    }
}
