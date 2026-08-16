/*
 * adpcm.h - Packet-independent IMA ADPCM codec.
 *
 * Byte-for-byte compatible with the companion implementation in
 * companion/IntercomCompanion/Audio/ImaAdpcm.cs.
 * Each frame is self-contained: it carries the predictor and step index that
 * were in effect BEFORE its first sample, so a single lost packet never
 * corrupts the decoding of later packets.
 *
 * Frame layout (164 bytes for a 320-sample / 20 ms frame):
 *   int16  predictor    (big-endian)
 *   uint8  step_index
 *   uint8  reserved (= 0)
 *   uint8  nibbles[160]  (320 samples, 4 bits each, low nibble first)
 */
#ifndef INTERCOM_ADPCM_H
#define INTERCOM_ADPCM_H

#include <stdint.h>
#include <stddef.h>

#define ADPCM_FRAME_SAMPLES  320
#define ADPCM_HEADER_LEN     4
#define ADPCM_PAYLOAD_LEN    (ADPCM_HEADER_LEN + ADPCM_FRAME_SAMPLES / 2) /* 164 */

/* Continuous encoder state; persists across frames within one PTT session. */
typedef struct {
    int32_t predictor;   /* current predicted sample                     */
    int32_t index;       /* current step-table index [0..88]             */
} adpcm_state_t;

void adpcm_state_reset(adpcm_state_t *st);

/*
 * Encode ADPCM_FRAME_SAMPLES PCM samples into `out` (must hold
 * ADPCM_PAYLOAD_LEN bytes).  The frame stamps the state as it was on entry,
 * then advances *st for the next frame.
 */
void adpcm_encode_frame(adpcm_state_t *st, const int16_t *pcm, uint8_t *out);

/*
 * Decode one ADPCM_PAYLOAD_LEN-byte frame into ADPCM_FRAME_SAMPLES samples.
 * Self-contained: seeds its state from the frame header.  Returns 0 on success,
 * -1 if `len` is wrong.
 */
int adpcm_decode_frame(const uint8_t *in, size_t len, int16_t *pcm);

#endif /* INTERCOM_ADPCM_H */
