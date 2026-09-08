using IntercomCompanion.Core;
using System.Net;

foreach (byte caps in new byte[] { 0, 1, 2, 4, 3, 5, 6, 7, 0x80, 0xff })
{
    var hello = Protocol.ParseHello(Protocol.BuildHelloPayload("Kitchen", "1.2.3", 7, caps));
    Check.That(hello.Capabilities == caps && hello.ProtocolVersion == 3 && hello.Flags == 7, "HELLO bits roundtrip");
}
var wire = Protocol.Pack(Protocol.DefaultMeshId, 0x11223344, PacketType.Claim, 0x55667788, 0, 0, [], Protocol.DirectedFlag);
Check.That(Convert.ToHexString(wire) == "50545431010100204D4553481122334455667788000000000000000000000003", "C/C# golden big-endian packet");
wire[31] = 2;
Check.That(!Protocol.TryParse(wire, 1, out _), "obsolete control rejected");
wire[4] = (byte)PacketType.Hello;
Check.That(Protocol.TryParse(wire, 1, out _), "obsolete discovery can be displayed");
var service = new Peer(42, new(IPAddress.Loopback, Protocol.Port), "Assistant", 3, "test", 2, 0, DateTimeOffset.UtcNow);
var client = service with { NodeId = 99, Capabilities = 4 };
var options = AssistantServices.Choices([service, client], "MESH", 43);
Check.That(options.Count == 3 && options.Any(c => c.SenderId == 42 && c.Available) && options.Any(c => c.SenderId == 43 && !c.Available), "only services plus preserved offline selection");
Check.That(AssistantServices.CanConfigure(client) && !AssistantServices.CanConfigure(service), "capability gates controls");
Check.That(AssistantServices.Resolve([], 42, "MESH") is null && AssistantServices.Resolve([service], 43, "MESH") is null, "no fallback service");
var moved = service with { Endpoint = new(IPAddress.Parse("127.0.0.9"), Protocol.Port) };
Check.That(AssistantServices.Resolve([moved], 42, "MESH")?.Endpoint.Equals(moved.Endpoint) == true, "stable ID resolves current endpoint");
Check.That(!new DeviceConfiguration("old", 512, 48, false, 0, false, false, "MESH", 1).AssistantEnabled, "old config default disabled");
Check.That(!(service with { ProtocolVersion = 2 }).IsProtocolCompatible, "p2 peer incompatible");
Check.That((service with { Capabilities = 1 }).IsOtaEligible, "OTA bit preserved");

var configured = new DeviceConfiguration("Kitchen", 700, 80, true, 180, true, false, "HOME", 123, true, 42);
var encoded = DeviceConfigurationJson.Encode(configured);
using (var configJson = System.Text.Json.JsonDocument.Parse(encoded.Replace("\"cmd\":\"set\"", "\"type\":\"config\"")))
    Check.That(DeviceConfigurationJson.Decode(configJson.RootElement, 999) == configured, "all config fields roundtrip including stable service ID");
using (var oldConfig = System.Text.Json.JsonDocument.Parse("{\"type\":\"config\",\"alias\":\"Old\"}"))
{
    var decoded = DeviceConfigurationJson.Decode(oldConfig.RootElement, 123);
    Check.That(!decoded.AssistantEnabled && decoded.AssistantServiceId == 0 && decoded.DeviceId == 123, "missing assistant fields safe defaults");
}
Check.That(!encoded.Contains("password"), "remote config does not disclose passwords");

await using (var net = new SimNetwork(4))
{
    net.Sessions[0].PressBroadcast(); net.Sessions[1].PressBroadcast();
    await net.Run();
    Check.That(net.Sessions.Count(s => s.State == IntercomState.Talking) == 1, "one broadcast winner");
}
await using (var net = new SimNetwork(6))
{
    var s = net.Sessions; var n = net.Nodes;
    s[0].PressSelected(n[1].Self); s[2].PressSelected(n[3].Self);
    await net.Run();
    Check.That(s[0].State == IntercomState.Talking && s[2].State == IntercomState.Talking && s[1].ReceptionDirected && s[3].ReceptionDirected, "independent directed sessions");
    Check.That(s[4].State == IntercomState.Idle, "unrelated node not reserved");
    s[4].PressBroadcast(); await net.Run();
    Check.That(s[4].State == IntercomState.Talking && s[5].State == IntercomState.Receiving && !s[5].ReceptionDirected, "broadcast to available node");
    Check.That(s[0].State == IntercomState.Talking && s[1].LastTalker?.NodeId == 1 && s[2].State == IntercomState.Talking, "directed survives broadcast");
    var broadcast = net.Sent.First(p => p.Type == PacketType.Claim && p.Flags == 0);
    n[1].Media(new RtpPacket(1, 0, broadcast.SessionId, new byte[164]), n[4].Self.Endpoint);
    Check.That(!s[1].ReceptionHasAudio, "directed receiver ignores broadcast RTP");
    n[1].Deliver(broadcast with { Type = PacketType.End }, n[4].Self.Endpoint);
    Check.That(s[1].State == IntercomState.Receiving && s[1].ReceptionDirected, "broadcast END cannot end directed RX");
}
await using (var net = new SimNetwork(4))
{
    var s = net.Sessions; var n = net.Nodes;
    s[0].PressBroadcast(); await net.Run();
    s[2].PressSelected(n[1].Self); await net.Run();
    Check.That(s[1].ReceptionDirected && s[1].LastTalker?.NodeId == 3, "directed preempts broadcast reception");
    Check.That(s[0].State == IntercomState.Talking && !s[3].ReceptionDirected && s[3].LastTalker?.NodeId == 1, "original broadcast continues");
    s[1].PressBroadcast(); await net.Run();
    Check.That(s[1].State == IntercomState.WaitingForFloor, "broadcast owner remembered during directed RX");
}
await using (var net = new SimNetwork(4))
{
    var s = net.Sessions; var n = net.Nodes;
    s[0].PressSelected(n[1].Self); await net.Run();
    s[2].PressSelected(n[1].Self); await net.Run();
    Check.That(s[2].State == IntercomState.Idle && s[0].State == IntercomState.Talking && s[1].LastTalker?.NodeId == 1, "endpoint collision rejected");
    s[2].ReleasePtt(); s[2].PressSelected(n[3].Self); await net.Run();
    Check.That(s[2].State == IntercomState.Talking && s[3].ReceptionDirected, "rejected caller can use unrelated endpoint");
}
await using (var net = new SimNetwork(3))
{
    var s = net.Sessions; var n = net.Nodes;
    s[1].PressBroadcast(); await net.Run();
    s[0].PressSelected(n[1].Self); await net.Run();
    Check.That(s[1].State == IntercomState.Talking && s[0].State == IntercomState.Idle, "local TX rejects directed request");
}
await using (var net = new SimNetwork(2))
{
    net.DropAccept = true;
    net.Sessions[0].PressSelected(net.Nodes[1].Self); await net.Run();
    Check.That(net.Sessions[0].State == IntercomState.Idle, "lost ACCEPT fails closed");
    await net.Run(800);
    Check.That(net.Sessions[1].State == IntercomState.Idle, "orphan reservation times out");
}
await using (var net = new SimNetwork(2))
{
    net.Sessions[0].PressSelected(net.Nodes[1].Self);
    net.Sessions[1].PressSelected(net.Nodes[0].Self);
    await net.Run();
    Check.That(net.Sessions.Count(s => s.State == IntercomState.Talking) <= 1 &&
        net.Sent.Any(p => p.Type == PacketType.Busy && p.Flags == Protocol.DirectedFlag), "crossed directed claims cannot establish conflicting transmitters");
    foreach (var session in net.Sessions) session.ReleasePtt();
    await net.Run(850);
    Check.That(net.Sessions.All(s => s.State == IntercomState.Idle), "crossed/retried reservations release or expire");
}
await using (var net = new SimNetwork(3))
{
    var s = net.Sessions; var n = net.Nodes;
    s[0].PressBroadcast(); await net.Run();
    var claim = net.Sent.First(p => p.Type == PacketType.Claim);
    n[1].Deliver(claim with { Type = PacketType.End, Flags = Protocol.DirectedFlag }, n[0].Self.Endpoint);
    s[2].PressBroadcast();
    Check.That(s[2].State == IntercomState.WaitingForFloor, "active broadcaster makes another broadcast wait");
    await net.Run(700);
    Check.That(s[0].State == IntercomState.Talking && s[2].State == IntercomState.Receiving, "occupied broadcast press expires after 500ms");
    Check.That(s[1].State == IntercomState.Receiving, "directed END cannot drain broadcast receiver");
}
await using (var net = new SimNetwork(2))
{
    net.Sessions[0].PressBroadcast(); await net.Run();
    net.Sessions[1].PressBroadcast(); await net.Run(50);
    net.Sessions[0].ReleasePtt(); await net.Run();
    Check.That(net.Sessions[1].State == IntercomState.Talking, "waiting broadcast begins when floor releases inside 500ms");
}
await using (var net = new SimNetwork(3))
{
    net.DropAccept = true;
    net.Sessions[0].PressSelected(net.Nodes[1].Self); await net.Run(20);
    var claim = net.Sent.First(p => p.Type == PacketType.Claim);
    net.Nodes[0].Deliver(claim with { Type = PacketType.Accept, SenderId = 3 }, net.Nodes[2].Self.Endpoint);
    await net.Run();
    Check.That(net.Sessions[0].State == IntercomState.Idle, "unrelated ACCEPT cannot grant a directed reservation");
}
Console.WriteLine($"PASS: {Check.Count} assertions (production companion state machine, wire codecs and assistant selection)");
