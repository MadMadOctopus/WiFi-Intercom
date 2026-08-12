using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.IO.Ports;
using Concentus;
using Concentus.Enums;
using static CaptureFormat;

const uint MeshId = 0x4D455348; // MESH
const uint ToolNodeId = 0xD1A60002;
const int ControlPort = 45678;
const int RtpPort = 45679;
var multicast = IPAddress.Parse("239.255.42.99");

var options = CaptureOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);
var sourcePath = Path.Combine(options.OutputDirectory, "source-conditioned.wav");
var transportPath = Path.Combine(options.OutputDirectory, "transport-decoded.wav");

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
using var source = new RawSourceRecorder(sourcePath);
using var transport = new TransportRecorder(transportPath);
using var serial = new SerialPort(options.Port, 921600) { ReadTimeout = 250, WriteTimeout = 250 };
using var control = CreateControlSocket(multicast);
using var media = CreateMediaSocket();

serial.Open();
Console.WriteLine($"Recording COM {options.Port} and RTP for {options.DeviceId:x8} for {options.Seconds}s.");
Console.WriteLine("Press and hold COM6 Broadcast once for 3-8 seconds. Do not run the companion during this capture.");

var controlTask = ControlLoopAsync(control, source, transport, options.DeviceId, cancellation.Token);
var mediaTask = MediaLoopAsync(media, transport, cancellation.Token);
var rawTask = RawLoopAsync(serial, source, cancellation.Token);
var helloTask = HelloLoopAsync(control, multicast, cancellation.Token);

try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token); }
catch (OperationCanceledException) { }

try { serial.Close(); } catch { }
try { await Task.WhenAll(controlTask, mediaTask, rawTask, helloTask); } catch (OperationCanceledException) { }
source.Finish();
transport.Finish();
Console.WriteLine();
Console.WriteLine(source.Summary);
Console.WriteLine(transport.Summary);
Console.WriteLine($"Wrote:\n  {sourcePath}\n  {transportPath}");

static UdpClient CreateControlSocket(IPAddress multicast)
{
    var socket = new UdpClient(AddressFamily.InterNetwork);
    socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Client.Bind(new IPEndPoint(IPAddress.Any, ControlPort));
    socket.JoinMulticastGroup(multicast, 1);
    socket.Ttl = 1;
    return socket;
}

static UdpClient CreateMediaSocket()
{
    var socket = new UdpClient(AddressFamily.InterNetwork);
    socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Client.Bind(new IPEndPoint(IPAddress.Any, RtpPort));
    return socket;
}

static async Task HelloLoopAsync(UdpClient socket, IPAddress multicast, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var packet = PackControl(5, 0, 0, "capture-tool"u8);
        await socket.SendAsync(packet, new IPEndPoint(multicast, ControlPort), cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }
}

static async Task ControlLoopAsync(UdpClient socket, RawSourceRecorder source, TransportRecorder transport,
                                   uint deviceId, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var received = await socket.ReceiveAsync(cancellationToken);
        var data = received.Buffer;
        if (data.Length < 32 || !data.AsSpan(0, 4).SequenceEqual("PTT1"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4)) != MeshId ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4)) != deviceId)
            continue;
        var type = data[4];
        var session = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
        if (type == 1 && transport.TryStart(session))
        {
            source.Start();
            Console.WriteLine($"Claim received: session {session:x8}; capturing source and transport.");
        }
        else if (type == 4 && transport.IsSession(session))
        {
            source.StopAfter(TimeSpan.FromMilliseconds(300));
            transport.MarkEnded();
            Console.WriteLine("END received; draining diagnostic streams.");
        }
    }
}

static async Task MediaLoopAsync(UdpClient socket, TransportRecorder transport, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var received = await socket.ReceiveAsync(cancellationToken);
        var data = received.Buffer;
        if (data.Length <= 12 || data[0] != 0x80 || (data[1] & 0x7f) != 111) continue;
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
        var session = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
        transport.Accept(session, sequence, data.AsSpan(12));
    }
}

static async Task RawLoopAsync(SerialPort serial, RawSourceRecorder source, CancellationToken cancellationToken)
{
    var parser = new RawMicParser(source);
    var buffer = new byte[4096];
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var count = await serial.BaseStream.ReadAsync(buffer, cancellationToken);
            if (count > 0) parser.Append(buffer.AsSpan(0, count));
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) when (cancellationToken.IsCancellationRequested) { }
}

static byte[] PackControl(byte type, uint session, uint sequence, ReadOnlySpan<byte> payload)
{
    var packet = new byte[32 + payload.Length];
    "PTT1"u8.CopyTo(packet);
    packet[4] = type;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 32);
    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), MeshId);
    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), ToolNodeId);
    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), session);
    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20, 4), sequence);
    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24, 4), unchecked((uint)Environment.TickCount64));
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(28, 2), checked((ushort)payload.Length));
    payload.CopyTo(packet.AsSpan(32));
    return packet;
}

static class CaptureFormat
{
    public const int FrameSamples = 320;
    public const int FrameBytes = FrameSamples * sizeof(short);
    public const int RawPacketBytes = 4 + 4 + 2 + FrameBytes + 2;
}

sealed class CaptureOptions(string port, uint deviceId, int seconds, string outputDirectory)
{
    public string Port { get; } = port;
    public uint DeviceId { get; } = deviceId;
    public int Seconds { get; } = seconds;
    public string OutputDirectory { get; } = outputDirectory;

    public static CaptureOptions Parse(string[] args)
    {
        string port = "COM6", output = Path.Combine(AppContext.BaseDirectory, "capture");
        uint deviceId = 0x90677b2c;
        var seconds = 45;
        for (var index = 0; index < args.Length; index++)
        {
            string Next() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"Missing value after {args[index]}");
            switch (args[index])
            {
                case "--port": port = Next(); break;
                case "--device-id": deviceId = ParseUInt32(Next()); break;
                case "--seconds": seconds = int.Parse(Next()); break;
                case "--output": output = Next(); break;
                default: throw new ArgumentException($"Unknown option {args[index]}");
            }
        }
        return new CaptureOptions(port, deviceId, seconds, output);
    }

    private static uint ParseUInt32(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt32(value[2..], 16) : Convert.ToUInt32(value);
}

sealed class RawMicParser(RawSourceRecorder recorder)
{
    private readonly List<byte> pending = [];
    public void Append(ReadOnlySpan<byte> data)
    {
        pending.AddRange(data.ToArray());
        while (true)
        {
            var start = FindMagic();
            if (start < 0)
            {
                if (pending.Count > 3) pending.RemoveRange(0, pending.Count - 3);
                return;
            }
            if (start > 0) pending.RemoveRange(0, start);
            if (pending.Count < RawPacketBytes) return;
            var packet = pending.Take(RawPacketBytes).ToArray();
            if (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2)) != FrameBytes ||
                Crc16(packet.AsSpan(0, 10 + FrameBytes)) != BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10 + FrameBytes, 2)))
            {
                recorder.CorruptPacket();
                pending.RemoveAt(0);
                continue;
            }
            recorder.Accept(BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4)), packet.AsSpan(10, FrameBytes));
            pending.RemoveRange(0, RawPacketBytes);
        }
    }

    private int FindMagic()
    {
        for (var i = 0; i + 4 <= pending.Count; i++)
            if (pending[i] == (byte)'M' && pending[i + 1] == (byte)'I' && pending[i + 2] == (byte)'C' && pending[i + 3] == (byte)'P') return i;
        return -1;
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xffff;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}

sealed class RawSourceRecorder(string path) : IDisposable
{
    private readonly PcmWaveWriter wav = new(path);
    private bool active;
    private DateTimeOffset stopAt;
    private uint? previous;
    private long frames, gaps, corrupt;
    public void Start() { active = true; stopAt = DateTimeOffset.MaxValue; previous = null; }
    public void StopAfter(TimeSpan delay) => stopAt = DateTimeOffset.UtcNow + delay;
    public void Accept(uint sequence, ReadOnlySpan<byte> pcm)
    {
        if (!active) return;
        if (DateTimeOffset.UtcNow >= stopAt) { active = false; return; }
        if (previous is uint old && sequence > old + 1) { gaps += sequence - old - 1; wav.WriteSilence(checked((int)Math.Min(sequence - old - 1, 100))); }
        previous = sequence;
        wav.Write(pcm);
        frames++;
    }
    public void CorruptPacket() => corrupt++;
    public string Summary => $"Source: {frames} frames ({frames / 50.0:F2}s), USB gaps {gaps}, corrupt frames {corrupt}.";
    public void Finish() => wav.Finish();
    public void Dispose() => wav.Dispose();
}

sealed class TransportRecorder(string path) : IDisposable
{
    private readonly PcmWaveWriter wav = new(path);
    private readonly SortedDictionary<ushort, byte[]> packetsBySequence = [];
    private uint session;
    private bool started, ended;
    private long packets, duplicates, missing;
    public bool TryStart(uint newSession)
    {
        if (started) return false;
        session = newSession; started = true;
        return true;
    }
    public bool IsSession(uint value) => started && value == session;
    public void MarkEnded() => ended = true;
    public void Accept(uint packetSession, ushort sequence, ReadOnlySpan<byte> payload)
    {
        if (!IsSession(packetSession) || packetsBySequence.ContainsKey(sequence)) { if (IsSession(packetSession)) duplicates++; return; }
        packetsBySequence[sequence] = payload.ToArray();
        packets++;
    }
    public void Finish()
    {
        if (packetsBySequence.Count > 0)
        {
            var decoder = OpusCodecFactory.CreateDecoder(16000, 1, null);
            short[]? last = null;
            var expected = packetsBySequence.Keys.Min();
            var lastSequence = packetsBySequence.Keys.Max();
            while (true)
            {
                if (packetsBySequence.Remove(expected, out var payload))
                {
                    var pcm = new short[FrameSamples];
                    var count = decoder.Decode(payload, pcm, pcm.Length, false);
                    if (count == FrameSamples) { wav.Write(pcm); last = pcm; }
                    else { missing++; wav.Write(last is null ? new short[FrameSamples] : last.Select(sample => (short)(sample / 2)).ToArray()); }
                }
                else { missing++; wav.Write(last is null ? new short[FrameSamples] : last.Select(sample => (short)(sample / 2)).ToArray()); }
                if (expected == lastSequence) break;
                expected++;
            }
        }
        wav.Finish();
    }
    public string Summary => $"Transport: {packets} RTP packets ({packets / 50.0:F2}s), sequence gaps {missing}, duplicates {duplicates}, END seen {ended}.";
    public void Dispose() => wav.Dispose();
}

sealed class PcmWaveWriter : IDisposable
{
    private readonly FileStream stream;
    private long dataBytes;
    private bool finished;
    public PcmWaveWriter(string path)
    {
        stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        stream.Write(new byte[44]); // reserve the header; never overwrite audio data
    }
    public void Write(ReadOnlySpan<byte> pcm) { stream.Write(pcm); dataBytes += pcm.Length; }
    public void Write(short[] pcm) { var bytes = new byte[FrameBytes]; Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length); Write(bytes); }
    public void WriteSilence(int frames) => Write(new byte[frames * FrameBytes]);
    public void Finish()
    {
        if (finished) return;
        finished = true;
        stream.Position = 0;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36 + dataBytes)));
        "WAVEfmt "u8.CopyTo(header[8..]); BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1); BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 16000); BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 2); BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]); BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)dataBytes));
        stream.Write(header); stream.Flush(); stream.Position = stream.Length;
    }
    public void Dispose() { Finish(); stream.Dispose(); }
}
