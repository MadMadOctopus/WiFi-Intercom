using IntercomCompanion.Core;

namespace IntercomCompanion.Views.Settings;

/// <summary>"Group and device IDs": the groups this companion has joined, the
/// identity/group-names note, and a red danger block for moving devices between
/// groups one at a time.</summary>
internal sealed class GroupIdsPage : SettingsPage
{
    public sealed record GroupRow(string Code, string Label, int Count, bool IsTarget, string Note);
    public sealed record Movable(uint NodeId, string Alias, string Id, string FromLabel, string Reach, Color ReachColor, bool Selectable);

    private readonly TableLayoutPanel groupRows = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = UiStyles.White, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly TextBox joinCode = new() { MaxLength = 4, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox joinName = new() { MaxLength = 24 };
    private readonly Button joinButton = UiKit.PlainButton("Join");
    private readonly Label announcingHint = new() { AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Muted, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
    private readonly Label companionId = new() { AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };

    private readonly FlowLayoutPanel movableList = new() { AutoSize = false, Width = 716, Height = 165, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiStyles.White, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly Dictionary<uint, CheckBox> movableChecks = [];
    private readonly ComboBox destination = new();
    private readonly TextBox confirm = new() { MaxLength = 4, CharacterCasing = CharacterCasing.Upper };
    private readonly DisabledTintButton moveButton = new() { Text = "Move devices", Enabled = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
    private readonly Label confirmLabel = new();
    private string groupsKey = "";
    private string destKey = "";
    private string movableKey = "";

    public GroupIdsPage() : base(
        "Group and device IDs",
        "A group is an intercom channel, not a radio network. Every node in a group must be on the same 2.4 GHz LAN and a group holds up to 16 devices. A device belongs to exactly one group; this companion can join several and hear all of them, but it broadcasts to one at a time.",
        760)
    {
        Stack.Controls.Add(BuildJoinedPanel());
        Stack.Controls.Add(BuildIdentityPanel());
        Stack.Controls.Add(BuildDangerBlock());

        joinCode.KeyPress += RejectNonAlphanumeric;
        joinCode.TextChanged += (_, _) => joinButton.Enabled = Protocol.IsValidMeshId(joinCode.Text);
        joinButton.Enabled = false;
        joinButton.Click += (_, _) => { if (Protocol.IsValidMeshId(joinCode.Text)) { JoinClicked?.Invoke(joinCode.Text, joinName.Text); joinCode.Clear(); joinName.Clear(); } };
        destination.TextChanged += (_, _) => UpdateMoveGate();
        destination.SelectedIndexChanged += (_, _) => UpdateMoveGate();
        confirm.TextChanged += (_, _) => UpdateMoveGate();
        moveButton.Click += (_, _) => { if (moveButton.Enabled) MoveClicked?.Invoke(); };
    }

    public event Action<string>? SetTargetClicked;
    public event Action<string>? RenameClicked;
    public event Action<string>? LeaveClicked;
    public event Action<string, string?>? JoinClicked;
    public event Action? MoveClicked;

    public IReadOnlyList<uint> CheckedDevices => movableChecks.Where(pair => pair.Value.Checked).Select(pair => pair.Key).ToArray();
    public string DestinationCode => (destination.SelectedItem as string ?? destination.Text).Trim().ToUpperInvariant();
    public void ResetMove() { confirm.Clear(); UpdateMoveGate(); }

    public void SetCompanionId(uint nodeId) => companionId.Text = $"{nodeId:x8} · generated once, unique on this PC";

    public void SetGroups(IReadOnlyList<GroupRow> rows, bool canLeave, bool broadcastAll)
    {
        // Rebuild only when the joined-group content changes; the 1 s refresh
        // otherwise flickers the panel.
        var key = $"{canLeave}#{broadcastAll}#" + string.Join(";", rows.Select(row => $"{row.Code}|{row.Label}|{row.Count}|{row.IsTarget}|{row.Note}"));
        if (key == groupsKey) return;
        groupsKey = key;

        groupRows.SuspendLayout();
        groupRows.Controls.Clear();
        groupRows.RowStyles.Clear();
        groupRows.RowCount = 0;
        AddGroupRow(BuildAllRow(broadcastAll));
        foreach (var row in rows) AddGroupRow(BuildGroupRow(row, canLeave));
        groupRows.ResumeLayout();
    }

    private void AddGroupRow(Control control)
    {
        groupRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        groupRows.Controls.Add(control, 0, groupRows.RowCount++);
    }

    private static Label SelectionDot(bool selected) => new()
    {
        Text = selected ? "●" : "○", AutoSize = true, Anchor = AnchorStyles.Left,
        Font = new Font("Segoe UI", 11f), ForeColor = selected ? UiStyles.Blue : UiStyles.ControlBorder,
        Cursor = Cursors.Hand, Margin = new Padding(0, 0, 8, 0),
    };

    private Control BuildAllRow(bool selected)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, BackColor = UiStyles.White, Padding = new Padding(18, 11, 18, 11), Margin = Padding.Empty, Cursor = Cursors.Hand };
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var dot = SelectionDot(selected);
        row.Controls.Add(dot, 0, 0);
        row.Controls.Add(new Label { Text = "All groups", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(UiStyles.BodyFont, FontStyle.Bold), ForeColor = UiStyles.Ink, Padding = new Padding(0, 2, 0, 0) }, 1, 0);
        row.Controls.Add(new Label { Text = "Hold to broadcast reaches every joined group (default)", AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Padding = new Padding(10, 2, 0, 0) }, 2, 0);
        void Pick(object? _, EventArgs __) => SetTargetClicked?.Invoke("");
        row.Click += Pick;
        dot.Click += Pick;
        row.Paint += (_, e) => { using var pen = new Pen(UiStyles.HairRule); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
        return row;
    }

    public void SetDestinations(IReadOnlyList<string> codes)
    {
        var key = string.Join(",", codes);
        if (key == destKey) return;
        destKey = key;
        var current = destination.Text;
        destination.BeginUpdate();
        destination.Items.Clear();
        foreach (var code in codes) destination.Items.Add(code);
        if (!string.IsNullOrEmpty(current)) destination.Text = current;
        else if (codes.Count > 0) destination.SelectedIndex = 0;
        destination.EndUpdate();
    }

    public void SetAnnouncingHint(string text) => announcingHint.Text = text;

    public void SetMovable(IReadOnlyList<Movable> devices)
    {
        // Rebuild only on change so the checked state and the list stop flickering
        // under the 1 s refresh.
        var key = string.Join(";", devices.Select(device => $"{device.NodeId}|{device.Alias}|{device.Id}|{device.FromLabel}|{device.Reach}|{device.Selectable}"));
        if (key == movableKey) return;
        movableKey = key;

        var previouslyChecked = CheckedDevices.ToHashSet();
        movableList.SuspendLayout();
        movableList.Controls.Clear();
        movableChecks.Clear();
        foreach (var device in devices) movableList.Controls.Add(BuildMovableRow(device, previouslyChecked.Contains(device.NodeId)));
        movableList.ResumeLayout();
        UpdateMoveGate();
    }

    private Control BuildGroupRow(GroupRow model, bool canLeave)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 5, RowCount = 1, BackColor = UiStyles.White, Padding = new Padding(18, 11, 18, 11), Margin = Padding.Empty, Cursor = Cursors.Hand };
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var dot = SelectionDot(model.IsTarget);
        void Pick(object? _, EventArgs __) => SetTargetClicked?.Invoke(model.Code);
        row.Click += Pick;
        dot.Click += Pick;
        row.Controls.Add(dot, 0, 0);
        var title = new Label { Text = model.Label, AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(UiStyles.BodyFont, FontStyle.Bold), ForeColor = UiStyles.Ink, Padding = new Padding(0, 2, 0, 0), Cursor = Cursors.Hand };
        title.Click += Pick;
        row.Controls.Add(title, 1, 0);
        row.Controls.Add(new Label { Text = $"{model.Count} of 16 devices", AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Padding = new Padding(0, 2, 0, 0) }, 2, 0);
        row.Controls.Add(new Label { Text = model.Note, AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Muted, Padding = new Padding(0, 2, 0, 0) }, 3, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        var rename = UiKit.PlainButton("Rename…");
        rename.Margin = new Padding(0, 0, 8, 0);
        rename.Click += (_, _) => RenameClicked?.Invoke(model.Code);
        var leave = UiKit.PlainButton("Leave");
        leave.Enabled = canLeave;
        leave.Click += (_, _) => LeaveClicked?.Invoke(model.Code);
        actions.Controls.Add(rename);
        actions.Controls.Add(leave);
        row.Controls.Add(actions, 4, 0);
        row.Paint += (_, e) => { using var pen = new Pen(UiStyles.HairRule); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
        return row;
    }

    private Control BuildMovableRow(Movable model, bool isChecked)
    {
        var row = new TableLayoutPanel { AutoSize = false, Width = 700, Height = 33, ColumnCount = 5, RowCount = 1, BackColor = UiStyles.White, Padding = new Padding(12, 0, 12, 0), Margin = Padding.Empty };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var check = new CheckBox { Checked = isChecked && model.Selectable, Enabled = model.Selectable, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0), Text = "" };
        check.CheckedChanged += (_, _) => UpdateMoveGate();
        movableChecks[model.NodeId] = check;
        row.Controls.Add(check, 0, 0);
        row.Controls.Add(new Label { Text = model.Alias, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Font = new Font(UiStyles.BodyFont, FontStyle.Bold), ForeColor = UiStyles.Ink, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
        row.Controls.Add(new Label { Text = model.Id, AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        row.Controls.Add(new Label { Text = model.FromLabel, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Muted, TextAlign = ContentAlignment.MiddleLeft }, 3, 0);
        row.Controls.Add(new Label { Text = model.Reach, AutoSize = true, Anchor = AnchorStyles.Right, Font = UiStyles.Hint, ForeColor = model.ReachColor }, 4, 0);
        row.Paint += (_, e) => { using var pen = new Pen(UiStyles.HairRule); e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
        return row;
    }

    private void UpdateMoveGate()
    {
        var ticked = CheckedDevices.Count;
        var code = DestinationCode;
        confirmLabel.Text = code.Length == 0 ? "Type the destination code to confirm" : $"Type {code} to confirm";
        moveButton.Text = ticked == 0 ? $"Move devices to {(code.Length == 0 ? "…" : code)}" : $"Move {ticked} {(ticked == 1 ? "device" : "devices")} to {(code.Length == 0 ? "…" : code)}";
        var enabled = ticked > 0 && Protocol.IsValidMeshId(code) && confirm.Text.Trim().ToUpperInvariant() == code;
        moveButton.Enabled = enabled;
        moveButton.BackColor = enabled ? UiStyles.Red : Color.FromArgb(208, 138, 138);
        moveButton.ForeColor = UiStyles.White;
        moveButton.Font = new Font(UiStyles.SecondaryFont, FontStyle.Bold);
        moveButton.Padding = new Padding(14, 7, 14, 7);
        moveButton.FlatAppearance.BorderColor = moveButton.BackColor;
    }

    private static void RejectNonAlphanumeric(object? sender, KeyPressEventArgs e)
    {
        if (char.IsControl(e.KeyChar)) return;
        var upper = char.ToUpperInvariant(e.KeyChar);
        if (!(upper is >= 'A' and <= 'Z' || upper is >= '0' and <= '9')) e.Handled = true;
    }

    private Control BuildJoinedPanel()
    {
        var card = UiKit.Card(760, Padding.Empty);
        var layout = new TableLayoutPanel { Width = 758, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, BackColor = UiStyles.White, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = UiStyles.White, Padding = new Padding(18, 13, 18, 13), Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Groups this companion has joined", AutoSize = true, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Margin = new Padding(0, 0, 10, 0) }, 0, 0);
        header.Controls.Add(new Label { Text = "The selected group is where Hold to broadcast sends", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Padding = new Padding(0, 2, 0, 0) }, 1, 0);
        header.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };

        UiStyles.StyleInput(joinCode, 96);
        UiStyles.StyleInput(joinName, 210);
        joinName.PlaceholderText = "Name on this PC (optional)";
        var footer = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.FromArgb(247, 248, 249), Padding = new Padding(18, 12, 18, 12), Margin = Padding.Empty };
        footer.Controls.Add(new Label { Text = "Join another group", AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Ink, Padding = new Padding(0, 5, 10, 0) });
        joinCode.Margin = new Padding(0, 0, 8, 0);
        joinName.Margin = new Padding(0, 0, 8, 0);
        joinButton.Margin = new Padding(0, 0, 10, 0);
        footer.Controls.Add(joinCode);
        footer.Controls.Add(joinName);
        footer.Controls.Add(joinButton);
        footer.Controls.Add(announcingHint);

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(groupRows, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildIdentityPanel()
    {
        var card = UiKit.Card(760, new Padding(20, 14, 20, 16), 12);
        var grid = UiKit.FormGrid(170);
        UiKit.AddField(grid, "This companion's ID", companionId);
        UiKit.AddField(grid, "Group names", new Label { Text = "Names are stored on this PC only. Devices carry the 4-character code, so another companion may show the same group under a different name.", AutoSize = true, MaximumSize = new Size(540, 0), Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Padding = new Padding(0, 4, 0, 0) });
        card.Controls.Add(grid);
        return card;
    }

    private Control BuildDangerBlock()
    {
        var block = new TableLayoutPanel { Width = 760, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, BackColor = UiStyles.RedTint, Margin = new Padding(0, 12, 0, 0) };
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
        header.Controls.Add(new Label { Text = "Move devices to another group", AutoSize = true, Font = UiStyles.PanelHeading, ForeColor = UiStyles.DarkRed, Margin = Padding.Empty });
        header.Controls.Add(new Label { Text = "Pick the devices to move. Each one reboots and leaves the group it is in now.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = Color.FromArgb(122, 32, 32), Margin = new Padding(0, 4, 0, 0) });
        header.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(242, 214, 214)); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };

        var body = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 758, Padding = new Padding(18, 16, 18, 16), Margin = Padding.Empty, BackColor = UiStyles.RedTint };
        foreach (var consequence in new[]
        {
            "· A moved device leaves its old group at once and can no longer hear anyone still in it.",
            "· Devices are written one at a time, over the network or by USB, and each one reboots.",
            "· A device you cannot reach right now keeps its current group until you get to it.",
            "· If you are not a member of the destination group, join it or the moved devices vanish from this window.",
        })
            body.Controls.Add(new Label { Text = consequence, AutoSize = true, MaximumSize = new Size(716, 0), Font = UiStyles.BodyFont, ForeColor = UiStyles.Body, Margin = new Padding(0, 0, 0, 6) });

        var fields = UiKit.FormGrid(170);
        fields.Margin = new Padding(0, 12, 0, 0);
        fields.BackColor = UiStyles.RedTint;
        movableList.Padding = Padding.Empty;
        var listWrap = new Panel { Width = 718, Height = 165, BackColor = UiStyles.White, Padding = Padding.Empty, Margin = Padding.Empty };
        listWrap.Paint += (_, e) => UiStyles.DrawBorder(e, listWrap);
        movableList.Dock = DockStyle.Fill;
        listWrap.Controls.Add(movableList);
        UiKit.AddField(fields, "Devices to move", listWrap);

        UiKit.StyleCombo(destination, 220);
        destination.DropDownStyle = ComboBoxStyle.DropDown;
        UiKit.AddField(fields, "Destination group", destination, UiKit.Hint("or type a new code — 4 characters, A–Z and 0–9"));
        UiStyles.StyleInput(confirm, 140);
        confirm.KeyPress += RejectNonAlphanumeric;
        // Manual row so the label can name the exact code the user must type.
        var confirmRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        confirmLabel.AutoSize = true;
        confirmLabel.Font = UiStyles.BodyFont;
        confirmLabel.ForeColor = UiStyles.Ink;
        confirmLabel.Anchor = AnchorStyles.Left;
        confirmLabel.Padding = new Padding(0, 7, 16, 7);
        confirmLabel.Margin = Padding.Empty;
        fields.Controls.Add(confirmLabel, 0, confirmRow);
        var confirmPanel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        confirm.Margin = new Padding(0, 4, 8, 4);
        confirmPanel.Controls.Add(confirm);
        fields.Controls.Add(confirmPanel, 1, confirmRow);

        var action = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 12, 0, 0), BackColor = UiStyles.RedTint };
        moveButton.Margin = Padding.Empty;
        action.Controls.Add(moveButton);
        action.Controls.Add(new Label { Text = "Enabled once the confirmation matches.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = Color.FromArgb(122, 32, 32), Margin = new Padding(12, 9, 0, 0) });

        body.Controls.Add(fields);
        body.Controls.Add(action);
        block.Controls.Add(header, 0, 0);
        block.Controls.Add(body, 0, 1);
        UpdateMoveGate();
        return block;
    }
}
