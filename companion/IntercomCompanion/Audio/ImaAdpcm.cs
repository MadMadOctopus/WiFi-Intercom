namespace IntercomCompanion.Audio;

/// <summary>IMA ADPCM matching firmware/main/adpcm.c byte-for-byte.</summary>
internal sealed class ImaAdpcm
{
    private static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253,
        279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166,
        1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
        4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289,
        16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    public const int SamplesPerFrame = 320;
    public const int PayloadLength = 164;
    private int predictor;
    private int index;

    public void Reset()
    {
        predictor = 0;
        index = 0;
    }

    public byte[] Encode(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length != SamplesPerFrame) throw new ArgumentException("A frame must contain 320 samples.", nameof(pcm));
        var payload = new byte[PayloadLength];
        payload[0] = unchecked((byte)(predictor >> 8));
        payload[1] = unchecked((byte)predictor);
        payload[2] = (byte)index;

        for (var sampleIndex = 0; sampleIndex < SamplesPerFrame; sampleIndex++)
        {
            var sample = pcm[sampleIndex];
            var step = StepTable[index];
            var difference = sample - predictor;
            var nibble = difference < 0 ? 8 : 0;
            if (difference < 0) difference = -difference;
            var estimate = step >> 3;
            if (difference >= step) { nibble |= 4; difference -= step; estimate += step; }
            step >>= 1;
            if (difference >= step) { nibble |= 2; difference -= step; estimate += step; }
            step >>= 1;
            if (difference >= step) { nibble |= 1; estimate += step; }
            predictor = Clamp((nibble & 8) != 0 ? predictor - estimate : predictor + estimate, short.MinValue, short.MaxValue);
            index = Clamp(index + IndexTable[nibble], 0, 88);
            var outputIndex = 4 + sampleIndex / 2;
            if ((sampleIndex & 1) == 0) payload[outputIndex] = (byte)nibble;
            else payload[outputIndex] |= (byte)(nibble << 4);
        }
        return payload;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out short[]? pcm)
    {
        pcm = null;
        if (payload.Length != PayloadLength) return false;
        var predictor = (int)(short)((payload[0] << 8) | payload[1]);
        var index = Clamp(payload[2], 0, 88);
        var samples = new short[SamplesPerFrame];
        for (var sampleIndex = 0; sampleIndex < SamplesPerFrame; sampleIndex++)
        {
            var source = payload[4 + sampleIndex / 2];
            var nibble = (sampleIndex & 1) == 0 ? source & 0x0F : source >> 4;
            var step = StepTable[index];
            var estimate = step >> 3;
            if ((nibble & 4) != 0) estimate += step;
            if ((nibble & 2) != 0) estimate += step >> 1;
            if ((nibble & 1) != 0) estimate += step >> 2;
            predictor = Clamp((nibble & 8) != 0 ? predictor - estimate : predictor + estimate, short.MinValue, short.MaxValue);
            index = Clamp(index + IndexTable[nibble], 0, 88);
            samples[sampleIndex] = (short)predictor;
        }
        pcm = samples;
        return true;
    }

    private static int Clamp(int value, int minimum, int maximum) => Math.Clamp(value, minimum, maximum);
}
