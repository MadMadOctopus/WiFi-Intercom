/*
 * protocol.h - UDP wire protocol for the intercom.
 *
 * Fixed 32-byte header, network byte order, packed explicitly (never a raw C
 * struct on the wire).  Identical layout to pc_app/app.py.
 */
#ifndef INTERCOM_PROTOCOL_H
#define INTERCOM_PROTOCOL_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#define PROTO_MAGIC0 'P'
#define PROTO_MAGIC1 'T'
#define PROTO_MAGIC2 'T'
#define PROTO_MAGIC3 '1'
#define PROTO_HEADER_LEN 32

/* Packet types. */
#define PKT_CLAIM 1
#define PKT_BUSY  2
#define PKT_AUDIO 3
#define PKT_END   4

typedef struct {
    uint8_t  type;
    uint8_t  flags;
    uint32_t mesh_id;
    uint32_t sender_id;
    uint32_t session_id;
    uint32_t sequence;
    uint32_t timestamp_ms;
    uint16_t payload_len;
    const uint8_t *payload;   /* points into the caller's receive buffer */
} intercom_pkt_t;

/*
 * Serialise a packet into `buf` (must hold PROTO_HEADER_LEN + payload_len).
 * Returns the total number of bytes written.
 */
size_t protocol_pack(uint8_t *buf, uint8_t type, uint32_t mesh_id,
                     uint32_t sender_id, uint32_t session_id, uint32_t sequence,
                     uint32_t timestamp_ms, const uint8_t *payload,
                     uint16_t payload_len);

/*
 * Validate and parse a received datagram.  Enforces magic, header length,
 * mesh id, payload-length sanity, and rejects our own sender id.  Returns true
 * on a valid packet (fields filled into *out), false otherwise.
 */
bool protocol_parse(const uint8_t *data, size_t len, uint32_t mesh_id,
                    uint32_t self_id, intercom_pkt_t *out);

#endif /* INTERCOM_PROTOCOL_H */
