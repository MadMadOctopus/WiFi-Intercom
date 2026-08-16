using IntercomCompanion.Core;

namespace IntercomCompanion.Views;

/// <summary>The device grid: header (title, count, filter box, chips) and a
/// three-column scrolling card grid. The card table is the only container in the
/// app that rebuilds its own children on refresh.</summary>
internal sealed class DeviceGridView : UserControl
{
    private readonly Label title = new() { Text = "Devices", AutoSize = true, Font = UiStyles.CardAlias, ForeColor = UiStyles.Ink, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) };
    private readonly Label count = new() { AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) };
    private readonly TextBox filter = new() { PlaceholderText = "Alias or ID" };
    private readonly TableLayoutPanel grid = new() { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 3, BackColor = UiStyles.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly Dictionary<uint, DeviceCard> cards = [];
    private readonly Dictionary<string, Chip> chips = [];
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 60 };

    private IReadOnlyList<Peer> peers = [];
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

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = UiStyles.Surface };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(grid, 0, 1);
        Controls.Add(root);

        pulse.Tick += (_, _) => StepPulse();
    }

    public event Action<Peer>? TalkPressed;
    public event Action? TalkReleased;
    public event Action<Peer>? SilenceClicked;
    public event Action<Control, Peer>? MoreClicked;
    public event Action<Peer, int>? VolumeCommitted;

    public void Update(IReadOnlyList<Peer> latest, string meshId, Func<uint, int> volumeLookup, bool disablePtt)
    {
        peers = latest;
        volumeFor = volumeLookup;
        pttDisabled = disablePtt;
        count.Text = $"{peers.Count} of 16 in group {meshId}";

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
            "Legacy" => peer.ProtocolVersion is null or 1,
            _ => true,
        };
        return searchOk && filterOk;
    }

    private void Refilter()
    {
        var visiblePeers = peers
            .Where(Matches)
            .OrderBy(peer => peer.Alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Only rebuild the card table when the visible set or its order actually
        // changes. Refreshing card data in place every second must not re-add
        // controls, or the grid flickers (spec 4.2/11).
        var key = string.Join(",", visiblePeers.Select(peer => peer.NodeId.ToString("x8")));
        if (key == layoutKey) return;
        layoutKey = key;

        var visible = visiblePeers.Select(peer => cards[peer.NodeId]).ToArray();
        grid.SuspendLayout();
        grid.Controls.Clear();
        grid.ColumnStyles.Clear();
        grid.RowStyles.Clear();
        for (var column = 0; column < 3; column++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        var cardRows = Math.Max(1, (visible.Length + 2) / 3);
        // A trailing filler row absorbs spare height so the card rows stay a fixed
        // height instead of the sole row stretching when only a few cards show.
        grid.RowCount = cardRows + 1;
        for (var row = 0; row < cardRows; row++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < visible.Length; i++)
        {
            visible[i].Dock = DockStyle.Fill;
            visible[i].Margin = new Padding(5);
            grid.Controls.Add(visible[i], i % 3, i / 3);
        }
        grid.ResumeLayout();
    }

    private void UpdateChipCounts()
    {
        chips["All"].Count = peers.Count;
        chips["Speaking"].Count = peers.Count(peer => peer.IsTalking);
        chips["Muted"].Count = peers.Count(peer => peer.HardwareMuted);
        chips["Silenced"].Count = peers.Count(peer => peer.SoftMuted);
        chips["Legacy"].Count = peers.Count(peer => peer.ProtocolVersion is null or 1);
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
