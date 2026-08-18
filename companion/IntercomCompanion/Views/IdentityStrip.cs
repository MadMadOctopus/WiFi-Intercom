using IntercomCompanion.Core;

namespace IntercomCompanion.Views;

/// <summary>Top 56 px identity band: state dot, companion identity with a
/// broadcast-target picker, mic/speaker names with a Change… link, and the
/// local-mute and Settings… actions.</summary>
internal sealed class IdentityStrip : UserControl
{
    private readonly CircleDot dot = new(9) { Anchor = AnchorStyles.Left };
    private readonly Label alias = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Margin = new Padding(0, 0, 0, 1) };
    private readonly Label metaStatic = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.Hint, ForeColor = UiStyles.Secondary, Margin = Padding.Empty };
    private readonly LinkLabel metaPicker = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(UiStyles.Hint, FontStyle.Bold), LinkColor = UiStyles.Blue, ActiveLinkColor = UiStyles.DeepBlue, Margin = new Padding(4, 0, 0, 0), Visible = false };
    private readonly Label micName = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Body, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
    private readonly Label speakerName = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Body, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
    private readonly CheckBox localMute = new()
    {
        Text = "Mute my speaker", Appearance = Appearance.Button, AutoSize = false,
        Width = 122, Height = 29, TextAlign = ContentAlignment.MiddleCenter, Font = UiStyles.SecondaryFont,
        FlatStyle = FlatStyle.Flat, BackColor = UiStyles.White, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0),
    };

    private CompanionSettings? settings;

    public IdentityStrip()
    {
        Dock = DockStyle.Fill;
        BackColor = UiStyles.White;
        Padding = new Padding(20, 0, 20, 0);

        localMute.FlatAppearance.BorderColor = UiStyles.ControlBorder;
        localMute.FlatAppearance.CheckedBackColor = UiStyles.HairRule;
        localMute.CheckedChanged += (_, _) => LocalMuteToggled?.Invoke(this, localMute.Checked);
        metaPicker.LinkClicked += (_, _) => ShowTargetMenu();

        var changeLink = new LinkLabel { Text = "Change…", AutoSize = true, Anchor = AnchorStyles.Left, Font = UiStyles.SecondaryFont, LinkColor = UiStyles.Blue, ActiveLinkColor = UiStyles.DeepBlue };
        changeLink.LinkClicked += (_, _) => ChangeAudioClicked?.Invoke(this, EventArgs.Empty);
        var settingsButton = UiKit.PlainButton("Settings…");
        settingsButton.Anchor = AnchorStyles.Left;
        settingsButton.Margin = Padding.Empty;
        settingsButton.Click += (_, _) => SettingsClicked?.Invoke(this, EventArgs.Empty);

        var metaRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        metaRow.Controls.Add(metaStatic);
        metaRow.Controls.Add(metaPicker);

        var identityBlock = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 0, 0) };
        identityBlock.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        identityBlock.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        identityBlock.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        identityBlock.Controls.Add(alias, 0, 0);
        identityBlock.Controls.Add(metaRow, 0, 1);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 9, RowCount = 1, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 9));   // dot
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // identity block
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // divider
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); // mic
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); // speaker
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // change link
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // spacer
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // mute
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // settings

        root.Controls.Add(dot, 0, 0);
        root.Controls.Add(identityBlock, 1, 0);
        root.Controls.Add(new VDivider(14, 11), 2, 0);
        root.Controls.Add(LabeledBlock("Mic", micName), 3, 0);
        root.Controls.Add(LabeledBlock("Speaker", speakerName), 4, 0);
        root.Controls.Add(changeLink, 5, 0);
        root.Controls.Add(localMute, 7, 0);
        root.Controls.Add(settingsButton, 8, 0);
        Controls.Add(root);
    }

    public event EventHandler? ChangeAudioClicked;
    public event EventHandler? SettingsClicked;
    public event EventHandler<bool>? LocalMuteToggled;
    public event Action<string>? BroadcastTargetSelected;
    public event EventHandler? GroupSettingsClicked;

    private static TableLayoutPanel LabeledBlock(string kicker, Label value)
    {
        var block = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 22, 0) };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        block.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        block.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        block.Controls.Add(new Label { Text = kicker.ToUpperInvariant(), AutoSize = false, Dock = DockStyle.Fill, Font = UiStyles.Meta, ForeColor = UiStyles.Muted, TextAlign = ContentAlignment.BottomLeft, Margin = Padding.Empty }, 0, 0);
        block.Controls.Add(value, 0, 1);
        return block;
    }

    public void Update(CompanionSettings companionSettings, string micText, string speakerText, Color stateColor)
    {
        settings = companionSettings;
        alias.Text = settings.Alias.ToUpperInvariant();
        var groups = settings.JoinedGroups;
        if (groups.Count == 1)
        {
            metaStatic.Text = $"ID {settings.NodeId:x8} · group {settings.GroupLabel(groups[0].Code)}";
            metaPicker.Visible = false;
        }
        else
        {
            metaStatic.Text = $"ID {settings.NodeId:x8} · {groups.Count} groups joined · broadcasting to";
            metaPicker.Text = $"{settings.BroadcastTargetLabel} ▾";
            metaPicker.Visible = true;
        }
        micName.Text = micText;
        speakerName.Text = speakerText;
        dot.DotColor = stateColor;
    }

    private void ShowTargetMenu()
    {
        if (settings is null) return;
        var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.Professional };
        menu.Renderer = new ToolStripProfessionalRenderer(new FlatMenuColors()) { RoundedEdges = false };
        menu.ShowImageMargin = true;
        menu.Font = UiStyles.BodyFont;
        var allItem = new ToolStripMenuItem("All groups") { Checked = settings.BroadcastsToAll, CheckOnClick = false };
        allItem.Click += (_, _) => BroadcastTargetSelected?.Invoke("");
        menu.Items.Add(allItem);
        foreach (var group in settings.JoinedGroups)
        {
            var code = group.Code;
            var item = new ToolStripMenuItem(settings.GroupLabel(code)) { Checked = !settings.BroadcastsToAll && code == settings.BroadcastTargetCode, CheckOnClick = false };
            item.Click += (_, _) => BroadcastTargetSelected?.Invoke(code);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var settingsItem = new ToolStripMenuItem("Group settings…");
        settingsItem.Click += (_, _) => GroupSettingsClicked?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(settingsItem);
        menu.Show(metaPicker, new Point(0, metaPicker.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiStyles.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}
