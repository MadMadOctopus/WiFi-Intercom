/*
 * adpcm.c - IMA ADPCM codec.  Mirrors pc_app/app.py exactly.
 */
#include "adpcm.h"

static const int16_t kStepTable[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
    19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
    50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
    130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
    337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
    876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
    2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
    5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
};

static const int8_t kIndexTable[16] = {
    -1, -1, -1, -1, 2, 4, 6, 8,
    -1, -1, -1, -1, 2, 4, 6, 8
};

static inline int32_t clampi(int32_t v, int32_t lo, int32_t hi)
{
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

void adpcm_state_reset(adpcm_state_t *st)
{
    st->predictor = 0;
    st->index = 0;
}

void adpcm_encode_frame(adpcm_state_t *st, const int16_t *pcm, uint8_t *out)
{
    int32_t start_pred = clampi(st->predictor, -32768, 32767);
    int32_t start_index = clampi(st->index, 0, 88);

    /* 4-byte frame header: predictor (big-endian int16), index, reserved. */
    out[0] = (uint8_t)((start_pred >> 8) & 0xFF);
    out[1] = (uint8_t)(start_pred & 0xFF);
    out[2] = (uint8_t)(start_index & 0xFF);
    out[3] = 0;

    int32_t predictor = start_pred;
    int32_t index = start_index;

    for (int i = 0; i < ADPCM_FRAME_SAMPLES; ++i) {
        int32_t step = kStepTable[index];
        int32_t diff = (int32_t)pcm[i] - predictor;
        int32_t sign = (diff < 0) ? 8 : 0;
        if (diff < 0) diff = -diff;

        int32_t delta = 0;
        int32_t vpdiff = step >> 3;
        if (diff >= step) { delta |= 4; diff -= step; vpdiff += step; }
        step >>= 1;
        if (diff >= step) { delta |= 2; diff -= step; vpdiff += step; }
        step >>= 1;
        if (diff >= step) { delta |= 1; vpdiff += step; }

        if (sign) predictor -= vpdiff;
        else      predictor += vpdiff;
        predictor = clampi(predictor, -32768, 32767);

        delta |= sign;
        index = clampi(index + kIndexTable[delta], 0, 88);

        uint8_t *slot = &out[ADPCM_HEADER_LEN + (i >> 1)];
        if (i & 1) *slot |= (uint8_t)((delta & 0x0F) << 4);
        else       *slot = (uint8_t)(delta & 0x0F);
    }

    st->predictor = predictor;
    st->index = index;
}

int adpcm_decode_frame(const uint8_t *in, size_t len, int16_t *pcm)
{
    if (len != ADPCM_PAYLOAD_LEN) return -1;

    int32_t predictor = (int16_t)((in[0] << 8) | in[1]); /* sign-extend */
    int32_t index = clampi(in[2], 0, 88);
    const uint8_t *nibbles = in + ADPCM_HEADER_LEN;

    for (int i = 0; i < ADPCM_FRAME_SAMPLES; ++i) {
        uint8_t byte = nibbles[i >> 1];
        int32_t delta = (i & 1) ? (byte >> 4) : (byte & 0x0F);

        int32_t step = kStepTable[index];
        int32_t vpdiff = step >> 3;
        if (delta & 4) vpdiff += step;
        if (delta & 2) vpdiff += step >> 1;
        if (delta & 1) vpdiff += step >> 2;

        if (delta & 8) predictor -= vpdiff;
        else           predictor += vpdiff;
        predictor = clampi(predictor, -32768, 32767);

        index = clampi(index + kIndexTable[delta], 0, 88);
        pcm[i] = (int16_t)predictor;
    }
    return 0;
}
