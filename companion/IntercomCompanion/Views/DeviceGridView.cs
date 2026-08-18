using IntercomCompanion.Core;

namespace IntercomCompanion.Views;

/// <summary>The device grid: header (title, count, filter box, chips) and a
/// scrolling body of one 3-column card grid per joined group. With a single
/// joined group there is no heading and it reads exactly as the single-group
/// design; with several, each group gets a heading and a broadcast-target chip.</summary>
internal sealed class DeviceGridView : UserControl
{
    private readonly Label title = new() { Text = "Devices", AutoSize = true, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) };
    private readonly Label count = new() { AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) };
    private readonly TextBox filter = new() { PlaceholderText = "Alias or ID" };
    private readonly Panel bodyScroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiStyles.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly FlowLayoutPanel sectionStack = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiStyles.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly Dictionary<uint, DeviceCard> cards = [];
    private readonly Dictionary<string, Chip> chips = [];
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 60 };

    private IReadOnlyList<Peer> peers = [];
    private CompanionSettings settings = null!;
    private Func<uint, int> volumeFor = _ => 512;
    private bool pttDisabled;
    private string activeFilter = "All";
    private string layoutKey = "";
    private float pulsePhase;

    public DeviceGridView()
    {
        Dock = DockStyle.Fill;
        BackColor = UiStyles.Surface;

        UiStyles.StyleInput(filter, 150);
        filter.Margin = new Padding(0, 0, 6, 0);
        filter.Anchor = AnchorStyles.Left;
        filter.TextChanged += (_, _) => Refilter();

        var filters = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        filters.Controls.Add(filter);
        foreach (var name in new[] { "All", "Speaking", "Muted", "Silenced", "Legacy" })
        {
            var chip = new Chip(name) { Active = name == "All" };
            chip.Selected += (_, _) => { activeFilter = name; Refilter(); UpdateChipStates(); };
            chips[name] = chip;
            filters.Controls.Add(chip);
        }

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, 10) };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(count, 1, 0);
        header.Controls.Add(filters, 3, 0);

        bodyScroll.Controls.Add(sectionStack);
        bodyScroll.Resize += (_, _) => FitStack();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = UiStyles.Surface };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(bodyScroll, 0, 1);
        Controls.Add(root);

        pulse.Tick += (_, _) => StepPulse();
    }

    public event Action<Peer>? TalkPressed;
    public event Action? TalkReleased;
    public event Action<Peer>? SilenceClicked;
    public event Action<Control, Peer>? MoreClicked;
    public event Action<Peer, int>? VolumeCommitted;
    public event Action<string>? BroadcastTargetClicked;

    public void Update(IReadOnlyList<Peer> latest, CompanionSettings companionSettings, Func<uint, int> volumeLookup, bool disablePtt)
    {
        settings = companionSettings;
        peers = latest.Where(peer => settings.IsJoined(peer.GroupCode)).ToArray();
        volumeFor = volumeLookup;
        pttDisabled = disablePtt;

        var groups = settings.JoinedGroups;
        count.Text = groups.Count == 1
            ? $"{peers.Count} of 16 in group {settings.GroupLabel(groups[0].Code)}"
            : $"{peers.Count} devices across {groups.Count} joined groups";

        var activeIds = peers.Select(peer => peer.NodeId).ToHashSet();
        foreach (var peer in peers)
        {
            if (!cards.TryGetValue(peer.NodeId, out var card))
            {
                card = new DeviceCard();
                var owned = card;
                card.TalkPressed += target => TalkPressed?.Invoke(target);
                card.TalkReleased += () => TalkReleased?.Invoke();
                card.SilenceClicked += target => SilenceClicked?.Invoke(target);
                card.MoreClicked += target => MoreClicked?.Invoke(owned, target);
                card.VolumeCommitted += (target, value) => VolumeCommitted?.Invoke(target, value);
                cards[peer.NodeId] = card;
            }
            card.UpdatePeer(peer, volumeFor(peer.NodeId), pttDisabled);
        }
        foreach (var stale in cards.Keys.Except(activeIds).ToArray())
        {
            cards[stale].Dispose();
            cards.Remove(stale);
        }

        UpdateChipCounts();
        UpdateChipStates();
        Refilter();

        var speaking = peers.Any(peer => peer.IsTalking);
        if (speaking && !pulse.Enabled) pulse.Start();
        else if (!speaking && pulse.Enabled) pulse.Stop();
    }

    private bool Matches(Peer peer)
    {
        var search = filter.Text.Trim();
        var searchOk = search.Length == 0
            || peer.Alias.Contains(search, StringComparison.OrdinalIgnoreCase)
            || peer.NodeId.ToString("x8").Contains(search, StringComparison.OrdinalIgnoreCase);
        var filterOk = activeFilter switch
        {
            "Speaking" => peer.IsTalking,
            "Muted" => peer.HardwareMuted,
            "Silenced" => peer.SoftMuted,
            "Legacy" => peer.IsLegacy,
            _ => true,
        };
        return searchOk && filterOk;
    }

    private void Refilter()
    {
        if (settings is null) return;
        var groups = settings.JoinedGroups;
        var multi = groups.Count > 1;

        // Rebuild only when the visible partition, order, or target changes.
        var key = settings.BroadcastTargetCode + "#" + string.Join("|", groups.Select(group =>
            group.Code + ":" + string.Join(",", peers
                .Where(peer => peer.GroupCode == group.Code && Matches(peer))
                .OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase)
                .Select(peer => peer.NodeId.ToString("x8")))));
        if (key == layoutKey) return;
        layoutKey = key;

        sectionStack.SuspendLayout();
        sectionStack.Controls.Clear();
        foreach (var group in groups)
        {
            var groupHasDevices = peers.Any(peer => peer.GroupCode == group.Code);
            if (!groupHasDevices) continue;
            if (multi) sectionStack.Controls.Add(BuildHeading(group.Code));
            var visible = peers
                .Where(peer => peer.GroupCode == group.Code && Matches(peer))
                .OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase)
                .Select(peer => cards[peer.NodeId])
                .ToArray();
            sectionStack.Controls.Add(BuildGrid(visible));
        }
        sectionStack.ResumeLayout();
        FitStack();
    }

    private Control BuildHeading(string code)
    {
        var isTarget = code == settings.BroadcastTargetCode;
        var heading = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, sectionStack.Controls.Count == 0 ? 0 : 18, 0, 8), BackColor = UiStyles.Surface };
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(new Panel { Width = 3, Height = 15, BackColor = isTarget ? UiStyles.Blue : Color.FromArgb(201, 204, 208), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 0, 0);
        heading.Controls.Add(new Label { Text = settings.GroupLabel(code), AutoSize = true, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 1, 0);
        heading.Controls.Add(new Label { Text = $"{peers.Count(peer => peer.GroupCode == code)} of 16 devices", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left }, 2, 0);

        if (isTarget)
        {
            // Clicking the current target toggles broadcasting back to all groups.
            var chip = new Label { Text = "BROADCAST TARGET", AutoSize = true, Font = new Font(UiStyles.Hint, FontStyle.Bold), ForeColor = UiStyles.White, BackColor = UiStyles.Ink, Padding = new Padding(8, 3, 8, 3), Anchor = AnchorStyles.Right, Cursor = Cursors.Hand };
            chip.Click += (_, _) => BroadcastTargetClicked?.Invoke("");
            heading.Controls.Add(chip, 3, 0);
        }
        else
        {
            var here = new LinkLabel { Text = "Broadcast here", AutoSize = true, Font = UiStyles.Hint, LinkColor = UiStyles.Blue, ActiveLinkColor = UiStyles.DeepBlue, Anchor = AnchorStyles.Right };
            here.LinkClicked += (_, _) => BroadcastTargetClicked?.Invoke(code);
            heading.Controls.Add(here, 3, 0);
        }
        return heading;
    }

    private TableLayoutPanel BuildGrid(DeviceCard[] visible)
    {
        // Explicit height + a width set by FitStack. AutoSize on a TableLayoutPanel
        // would shrink it to content and collapse the percent columns, which is
        // what narrowed the cards; the columns must divide the full row width.
        var rows = Math.Max(1, (visible.Length + 2) / 3);
        var grid = new TableLayoutPanel { ColumnCount = 3, RowCount = rows, AutoSize = false, Height = rows * 148, Margin = Padding.Empty, BackColor = UiStyles.Surface };
        for (var column = 0; column < 3; column++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        for (var row = 0; row < rows; row++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        for (var i = 0; i < visible.Length; i++)
        {
            visible[i].Dock = DockStyle.Fill;
            visible[i].Margin = new Padding(5);
            grid.Controls.Add(visible[i], i % 3, i / 3);
        }
        return grid;
    }

    private void FitStack()
    {
        var width = bodyScroll.ClientSize.Width;
        sectionStack.Width = width;
        foreach (Control child in sectionStack.Controls) child.Width = width;
    }

    private void UpdateChipCounts()
    {
        chips["All"].Count = peers.Count;
        chips["Speaking"].Count = peers.Count(peer => peer.IsTalking);
        chips["Muted"].Count = peers.Count(peer => peer.HardwareMuted);
        chips["Silenced"].Count = peers.Count(peer => peer.SoftMuted);
        chips["Legacy"].Count = peers.Count(peer => peer.IsLegacy);
    }

    private void UpdateChipStates()
    {
        foreach (var (name, chip) in chips) chip.Active = name == activeFilter;
    }

    private void StepPulse()
    {
        pulsePhase += 0.18f;
        foreach (var card in cards.Values) card.SetPulse(pulsePhase);
    }
}
