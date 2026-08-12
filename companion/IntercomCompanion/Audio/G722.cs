using NAudio.Codecs;
using IntercomCompanion.Core;

namespace IntercomCompanion.Audio;

/// <summary>20 ms, 16 kHz, 64 kb/s G.722 wrapper matching the ESP codec.</summary>
internal sealed class G722
{
    private readonly G722Codec codec = new();
    private G722CodecState state = new(64000, G722Flags.None);

    public void Reset() => state = new G722CodecState(64000, G722Flags.None);

    public byte[] Encode(short[] pcm)
    {
        if (pcm.Length != ImaAdpcm.SamplesPerFrame) throw new ArgumentException("Expected 320 samples.", nameof(pcm));
        var encoded = new byte[Rtp.G722PayloadLength];
        var count = codec.Encode(state, encoded, pcm, pcm.Length);
        if (count != encoded.Length) throw new InvalidOperationException($"Unexpected G.722 packet size: {count}.");
        return encoded;
    }

    public short[] Decode(byte[] payload)
    {
        if (payload.Length != Rtp.G722PayloadLength) throw new ArgumentException("Expected 160-byte RTP payload.", nameof(payload));
        var pcm = new short[ImaAdpcm.SamplesPerFrame];
        var count = codec.Decode(state, pcm, payload, payload.Length);
        if (count != pcm.Length) throw new InvalidOperationException($"Unexpected G.722 sample count: {count}.");
        return pcm;
    }
}
