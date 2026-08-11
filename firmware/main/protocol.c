/*
 * protocol.c - explicit big-endian packing/parsing of the 32-byte header.
 */
#include "protocol.h"
#include <string.h>

static inline void put_u16(uint8_t *p, uint16_t v)
{
    p[0] = (uint8_t)(v >> 8);
    p[1] = (uint8_t)(v & 0xFF);
}

static inline void put_u32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v >> 24);
    p[1] = (uint8_t)(v >> 16);
    p[2] = (uint8_t)(v >> 8);
    p[3] = (uint8_t)(v & 0xFF);
}

static inline uint16_t get_u16(const uint8_t *p)
{
    return (uint16_t)((p[0] << 8) | p[1]);
}

static inline uint32_t get_u32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
           ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

size_t protocol_pack(uint8_t *buf, uint8_t type, uint32_t mesh_id,
                     uint32_t sender_id, uint32_t session_id, uint32_t sequence,
                     uint32_t timestamp_ms, const uint8_t *payload,
                     uint16_t payload_len)
{
    buf[0] = PROTO_MAGIC0;
    buf[1] = PROTO_MAGIC1;
    buf[2] = PROTO_MAGIC2;
    buf[3] = PROTO_MAGIC3;
    buf[4] = type;
    buf[5] = 0;                       /* flags */
    put_u16(&buf[6], PROTO_HEADER_LEN);
    put_u32(&buf[8], mesh_id);
    put_u32(&buf[12], sender_id);
    put_u32(&buf[16], session_id);
    put_u32(&buf[20], sequence);
    put_u32(&buf[24], timestamp_ms);
    put_u16(&buf[28], payload_len);
    put_u16(&buf[30], 0);             /* reserved */

    if (payload && payload_len)
        memcpy(buf + PROTO_HEADER_LEN, payload, payload_len);
    return PROTO_HEADER_LEN + payload_len;
}

bool protocol_parse(const uint8_t *data, size_t len, uint32_t mesh_id,
                    uint32_t self_id, intercom_pkt_t *out)
{
    if (len < PROTO_HEADER_LEN) return false;
    if (data[0] != PROTO_MAGIC0 || data[1] != PROTO_MAGIC1 ||
        data[2] != PROTO_MAGIC2 || data[3] != PROTO_MAGIC3) return false;
    if (get_u16(&data[6]) != PROTO_HEADER_LEN) return false;
    if (get_u32(&data[8]) != mesh_id) return false;

    uint32_t sender_id = get_u32(&data[12]);
    if (sender_id == self_id) return false;         /* ignore our own packets */

    uint16_t payload_len = get_u16(&data[28]);
    if ((size_t)PROTO_HEADER_LEN + payload_len > len) return false;

    out->type = data[4];
    out->flags = data[5];
    out->mesh_id = mesh_id;
    out->sender_id = sender_id;
    out->session_id = get_u32(&data[16]);
    out->sequence = get_u32(&data[20]);
    out->timestamp_ms = get_u32(&data[24]);
    out->payload_len = payload_len;
    out->payload = data + PROTO_HEADER_LEN;
    return true;
}
