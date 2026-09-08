namespace IntercomCompanion.Core;

internal sealed record AssistantServiceChoice(uint SenderId, string Label, bool Available)
{
    public override string ToString() => Label;
}

internal static class AssistantServices
{
    public static bool CanConfigure(Peer device) => device.IsProtocolCompatible && device.SupportsAssistantClient;

    public static Peer? Resolve(IEnumerable<Peer> peers, uint serviceId, string groupCode) =>
        serviceId == 0 ? null : peers.FirstOrDefault(peer => peer.NodeId == serviceId &&
            peer.GroupCode == groupCode && peer.IsProtocolCompatible && peer.SupportsAssistantService &&
            DateTimeOffset.UtcNow - peer.LastSeen <= TimeSpan.FromSeconds(10));

    public static IReadOnlyList<AssistantServiceChoice> Choices(IEnumerable<Peer> peers, string groupCode, uint savedId)
    {
        var choices = new List<AssistantServiceChoice> { new(0, "Not selected", false) };
        choices.AddRange(peers.Where(peer => Resolve([peer], peer.NodeId, groupCode) is not null)
            .OrderBy(peer => peer.Alias).Select(peer => new AssistantServiceChoice(peer.NodeId,
                $"{peer.Alias} ({peer.NodeId:x8})", true)));
        if (savedId != 0 && choices.All(choice => choice.SenderId != savedId))
            choices.Add(new(savedId, $"Unavailable / offline ({savedId:x8})", false));
        return choices;
    }
}
