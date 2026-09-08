/*
 * app_main.c - Wi-Fi half-duplex push-to-talk intercom (ESP32-C3 peer).
 *
 * One node of a two-peer intercom.  The other peer is the PC application in
 * ../companion. Floor control/configuration use the compact PTT1 UDP protocol;
 * media uses RTP/IMA ADPCM over a separate UDP port.
 *
 * Tasks (FreeRTOS), decoupled by queues / a shared mutex so Wi-Fi, capture,
 * playback and button handling never block each other:
 *
 *   capture_task   ADC continuous @16kHz -> conditioned 320-sample frames -> mic_q
 *   tx_task        PTT + floor control; drains mic_q, encodes, sends RTP
 *   net_rx_task    receives PTT1 control and runs receive-side floor logic
 *   rtp_rx_task    receives RTP/IMA ADPCM and fills the jitter buffer
 *   playback_task  every 20 ms pops jitter buffer -> I2S (muted while we talk)
 *
 * Pin map (see README / config.h):
 *   Mic OUT  -> GPIO3  (ADC1_CH3)      PTT button -> GPIO20 (INPUT_PULLUP)
 *   I2S BCLK -> GPIO6  LRC -> GPIO7  DOUT -> GPIO5 (MAX98357A)
 */
#include <string.h>
#include <stdlib.h>
#include <stdio.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"

#include "esp_log.h"
#include "esp_system.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "esp_event.h"
#include "esp_app_desc.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "nvs_flash.h"

#include "driver/gpio.h"
#include "driver/i2s_std.h"
#include "driver/usb_serial_jtag.h"
#include "esp_adc/adc_continuous.h"

#include "lwip/sockets.h"

#include "config.h"
#include "adpcm.h"
#include "device_config.h"
#include "protocol.h"
#include "session_policy.h"
#include "ota_manager.h"
#include "ring_controller.h"
#include "usb_control.h"

/* The POC's call sites use these values extensively. They now resolve to the
 * NVS-backed production configuration loaded during boot. */
static device_config_t g_config;
#define MESH_ID (g_config.mesh_id)
#define NODE_ID (g_config.device_id)

static bool button_pressed(gpio_num_t pin);

static const char *TAG = "intercom";

/* ---- audio frame passed capture -> tx ---------------------------------- */
typedef struct { int16_t pcm[FRAME_SAMPLES]; } audio_frame_t;

/* Diagnostic-only source capture. The data is the exact conditioned PCM fed
 * to IMA ADPCM: ADC conversion, DC removal and MIC_GAIN have already
 * happened, while no codec or network operation has touched it. */
#ifndef INTERCOM_USB_RAW_MIC_CAPTURE
#define INTERCOM_USB_RAW_MIC_CAPTURE 0
#endif

#if INTERCOM_USB_RAW_MIC_CAPTURE
#define RAW_MIC_MAGIC          "MICP"
#define RAW_MIC_PAYLOAD_BYTES  (FRAME_SAMPLES * sizeof(int16_t))
#define RAW_MIC_PACKET_BYTES   (4 + 4 + 2 + RAW_MIC_PAYLOAD_BYTES + 2)
static QueueHandle_t raw_mic_q;

static uint16_t raw_mic_crc16(const uint8_t *data, size_t length)
{
    uint16_t crc = 0xffff; /* CRC-CCITT-FALSE */
    for (size_t i = 0; i < length; ++i) {
        crc ^= (uint16_t)data[i] << 8;
        for (int bit = 0; bit < 8; ++bit)
            crc = (crc & 0x8000) ? (uint16_t)((crc << 1) ^ 0x1021) : (uint16_t)(crc << 1);
    }
    return crc;
}

static void raw_mic_usb_task(void *arg)
{
    audio_frame_t frame;
    uint32_t sequence = 0;
    uint8_t packet[RAW_MIC_PACKET_BYTES];
    while (1) {
        if (xQueueReceive(raw_mic_q, &frame, portMAX_DELAY) != pdTRUE) continue;
        memcpy(packet, RAW_MIC_MAGIC, 4);
        packet[4] = (uint8_t)(sequence >> 24);
        packet[5] = (uint8_t)(sequence >> 16);
        packet[6] = (uint8_t)(sequence >> 8);
        packet[7] = (uint8_t)sequence++;
        packet[8] = (uint8_t)(RAW_MIC_PAYLOAD_BYTES >> 8);
        packet[9] = (uint8_t)RAW_MIC_PAYLOAD_BYTES;
        memcpy(packet + 10, frame.pcm, RAW_MIC_PAYLOAD_BYTES); /* little-endian PCM */
        uint16_t crc = raw_mic_crc16(packet, 10 + RAW_MIC_PAYLOAD_BYTES);
        packet[10 + RAW_MIC_PAYLOAD_BYTES] = (uint8_t)(crc >> 8);
        packet[11 + RAW_MIC_PAYLOAD_BYTES] = (uint8_t)crc;
        /* Never block capture/network: a saturated USB host drops diagnostic
         * frames, which the PC tool records as source-frame gaps. */
        usb_serial_jtag_write_bytes((const char *)packet, sizeof(packet), pdMS_TO_TICKS(2));
    }
}
#endif

/* ---- floor-control states ---------------------------------------------- */
typedef enum { ST_IDLE, ST_CLAIMING, ST_TALKING, ST_RECEIVING } node_state_t;

#define PEER_CAP 16
typedef struct {
    uint32_t id;
    struct sockaddr_in address;
    uint32_t last_seen_ms;
    uint8_t capabilities;
    uint8_t protocol_version;
} peer_t;

/* ---- jitter buffer ------------------------------------------------------ */
/* 32 decoded frames consume 20.7 KiB of static RAM. This leaves enough headroom
 * on C3 while accommodating Wi-Fi burst jitter beyond the 200 ms playout delay. */
#define JB_CAP 32
typedef struct {
    int16_t  pcm[FRAME_SAMPLES];
    uint16_t seq;                 /* RTP sequence, 16-bit and wrapping */
    bool     present;
} jb_slot_t;

/* ---- shared node state (guarded by g_lock) ----------------------------- */
static struct {
    node_state_t state;
    broadcast_floor_t broadcast;

    /* transmit */
    uint32_t tx_session;
    uint32_t tx_sequence;
    uint32_t tx_last_heartbeat_ms;
    int      tx_buffer_index;
    int      tx_buffer_count;
    uint16_t tx_rtp_sequence;
    uint32_t tx_rtp_timestamp;
    uint32_t claim_start_ms;
    int claims_sent;
    uint32_t last_claim_ms;
    bool     tx_directed;
    bool     tx_accepted;
    uint32_t tx_target_id;
    struct sockaddr_in tx_audio_destination;

    /* Pressing a PTT while another node owns the floor preserves only the
     * requested 500 ms. It is sent first if the floor becomes free in time. */
    bool     waiting_for_floor;
    bool     waiting_directed;
    struct sockaddr_in waiting_destination;
    uint32_t waiting_started_ms;
    int      waiting_count;
    audio_frame_t waiting_frames[BUSY_BUFFER_FRAMES];

    /* receive */
    bool     rx_directed;
    struct sockaddr_in rx_address;
    uint32_t rx_sender;
    uint32_t rx_session;
    uint32_t rx_last_ms;
    bool     rx_ending;      /* END received: drain remaining frames then idle */
    int      rx_drain;       /* playout iterations left before going idle      */
    uint32_t last_heartbeat_ms;
    uint32_t last_talker_id;
    bool     have_last_talker;
    struct sockaddr_in last_talker_address;
    uint32_t error_until_ms;

    /* Learned from HELLO/control source addresses. Broadcast RTP is mirrored
     * to these endpoints because Wi-Fi multicast delivery is often
     * lossy, while multicast remains the peerless discovery/floor channel. */
    peer_t peers[PEER_CAP];

    /* jitter buffer */
    jb_slot_t slots[JB_CAP];
    uint16_t  jb_expected;
    bool      jb_started;
    int16_t   jb_last[FRAME_SAMPLES];
    bool      jb_have_last;
    int       jb_missing_polls;
} g;

static SemaphoreHandle_t g_lock;
static QueueHandle_t     mic_q;

static int                 g_sock = -1;
static int                 g_rtp_sock = -1;
static struct sockaddr_in  g_group;
static uint32_t            g_wifi_ip_addr;

static i2s_chan_handle_t          i2s_tx;
static adc_continuous_handle_t    adc_handle;
static adpcm_state_t              g_adpcm_encoder;

/* ======================================================================== */
/* helpers                                                                   */
/* ======================================================================== */
static inline uint32_t now_ms(void)
{
    return (uint32_t)(esp_timer_get_time() / 1000);
}

static void send_packet_to(const struct sockaddr_in *destination, uint8_t type,
                           uint8_t flags, uint32_t session, uint32_t sequence,
                           const uint8_t *payload, uint16_t payload_len)
{
    /* stack-local: send_packet is called from several tasks concurrently */
    uint8_t buf[PROTO_HEADER_LEN + 768];
    size_t n = protocol_pack(buf, type, flags, MESH_ID, NODE_ID, session, sequence,
                             now_ms(), payload, payload_len);
    if (g_sock >= 0)
        sendto(g_sock, buf, n, 0, (const struct sockaddr *)destination,
               sizeof(*destination));
}

static void ota_status_send(const struct sockaddr_in *destination, uint32_t session,
                            const char *state, int progress, const char *message)
{
    char json[196];
    int length = snprintf(json, sizeof(json),
                          "{\"state\":\"%s\",\"progress\":%d,\"message\":\"%s\",\"version\":\"%s\"}",
                          state, progress, message, esp_app_get_description()->version);
    if (length > 0 && (size_t)length < sizeof(json))
        send_packet_to(destination, PKT_OTA_STATUS, 0, session, 0,
                       (const uint8_t *)json, (uint16_t)length);
}

/* Called from ota_task once an offer has been fully authenticated. Testing for
 * an idle floor and activating the OTA slot happen under g_lock as one step,
 * so a PTT cannot begin between the check and the claim: tx_task tests
 * ota_manager_is_active() under the same lock before starting a transmission. */
static bool ota_claim_floor(void)
{
    xSemaphoreTake(g_lock, portMAX_DELAY);
    bool idle = g.state == ST_IDLE;
    if (idle) ota_manager_set_active();
    xSemaphoreGive(g_lock);
    return idle;
}

/* Copies the still-live peers into `out` (room for PEER_CAP entries) and
 * returns how many there are, so senders never hold g_lock while transmitting.
 * Must be called without g_lock held. */
static int snapshot_active_peers(peer_t *out)
{
    uint32_t t = now_ms();
    int count = 0;
    xSemaphoreTake(g_lock, portMAX_DELAY);
    for (int i = 0; i < PEER_CAP; ++i) {
        if (g.peers[i].id != 0 && g.peers[i].protocol_version == INTERCOM_PROTOCOL_VERSION &&
            t - g.peers[i].last_seen_ms <= PEER_EXPIRE_MS)
            out[count++] = g.peers[i];
    }
    xSemaphoreGive(g_lock);
    return count;
}

static void send_to_active_peers(uint8_t type, uint8_t flags, uint32_t session,
                                 uint32_t sequence, const uint8_t *payload,
                                 uint16_t payload_len)
{
    peer_t peers[PEER_CAP];
    int count = snapshot_active_peers(peers);
    for (int i = 0; i < count; ++i)
        send_packet_to(&peers[i].address, type, flags, session, sequence,
                       payload, payload_len);
}

static void send_rtp_to(const struct sockaddr_in *control_destination,
                        uint32_t session, uint16_t sequence, uint32_t timestamp,
                        const uint8_t *payload, uint16_t payload_len)
{
    if (g_rtp_sock < 0 || payload_len != ADPCM_PAYLOAD_LEN) return;
    uint8_t packet[12 + ADPCM_PAYLOAD_LEN];
    packet[0] = 0x80;                       /* RTP v2, no extensions/CSRC */
    packet[1] = RTP_PAYLOAD_TYPE_ADPCM;     /* agreed dynamic IMA payload */
    packet[2] = (uint8_t)(sequence >> 8);
    packet[3] = (uint8_t)sequence;
    packet[4] = (uint8_t)(timestamp >> 24);
    packet[5] = (uint8_t)(timestamp >> 16);
    packet[6] = (uint8_t)(timestamp >> 8);
    packet[7] = (uint8_t)timestamp;
    packet[8] = (uint8_t)(session >> 24);   /* SSRC = PTT session */
    packet[9] = (uint8_t)(session >> 16);
    packet[10] = (uint8_t)(session >> 8);
    packet[11] = (uint8_t)session;
    memcpy(packet + 12, payload, payload_len);

    struct sockaddr_in destination = *control_destination;
    destination.sin_port = htons(RTP_PORT);
    sendto(g_rtp_sock, packet, 12 + payload_len, 0,
           (const struct sockaddr *)&destination, sizeof(destination));
}

static void send_rtp_to_active_peers(uint32_t session, uint16_t sequence,
                                     uint32_t timestamp, const uint8_t *payload,
                                     uint16_t payload_len)
{
    peer_t peers[PEER_CAP];
    int count = snapshot_active_peers(peers);
    for (int i = 0; i < count; ++i)
        send_rtp_to(&peers[i].address, session, sequence, timestamp,
                    payload, payload_len);
}

static void send_packet(uint8_t type, uint32_t session, uint32_t sequence,
                        const uint8_t *payload, uint16_t payload_len)
{
    xSemaphoreTake(g_lock, portMAX_DELAY);
    bool directed = g.tx_directed;
    struct sockaddr_in destination = g.tx_audio_destination;
    xSemaphoreGive(g_lock);
    if (directed)
        send_packet_to(&destination, type, PROTO_FLAG_DIRECTED, session, sequence, payload, payload_len);
    else
        send_to_active_peers(type, 0, session, sequence, payload, payload_len);
}

/* remote (session,sender) beats our claim if its tuple is strictly lower. */
static bool remote_wins(const intercom_pkt_t *p)
{
    if (p->session_id != g.tx_session)
        return p->session_id < g.tx_session;
    return p->sender_id < NODE_ID;
}

/* ======================================================================== */
/* jitter buffer (call with g_lock held)                                     */
/* ======================================================================== */
/* RTP sequence numbers are 16 bit and wrap every 65536 frames (~21.8 min of
 * continuous audio). Every ordering decision therefore uses a signed 16-bit
 * distance instead of a plain comparison: positive means `a` is ahead of `b`. */
static inline int jb_seq_delta(uint16_t a, uint16_t b)
{
    return (int)(int16_t)(uint16_t)(a - b);
}

static void jb_reset(void)
{
    for (int i = 0; i < JB_CAP; ++i) g.slots[i].present = false;
    g.jb_expected = 0;
    g.jb_started = false;
    g.jb_have_last = false;
    g.jb_missing_polls = 0;
}

static void jb_push(uint16_t seq, const int16_t *pcm)
{
    if (g.jb_started && jb_seq_delta(seq, g.jb_expected) < 0) return;  /* too late */

    /* A late I2S/DMA wake-up must not let a finite ring overwrite frames the
     * playout side still expects. Rejoin close to the live edge instead. */
    if (g.jb_started && jb_seq_delta(seq, g.jb_expected) >= JB_CAP) {
        for (int i = 0; i < JB_CAP; ++i) g.slots[i].present = false;
        g.jb_expected = (uint16_t)(seq - (JITTER_PREBUFFER - 1));
        g.jb_missing_polls = 0;
        ESP_LOGW(TAG, "RX jitter resynchronised at sequence %u", (unsigned)seq);
    }
    jb_slot_t *s = &g.slots[seq % JB_CAP];
    if (s->present && s->seq == seq) return;              /* duplicate */
    s->present = true;
    s->seq = seq;
    memcpy(s->pcm, pcm, sizeof(s->pcm));

    if (!g.jb_started) {
        /* count distinct buffered frames; start once we have the prebuffer */
        int cnt = 0;
        uint16_t minseq = 0;
        bool first = true;
        for (int i = 0; i < JB_CAP; ++i) {
            if (g.slots[i].present) {
                cnt++;
                if (first || jb_seq_delta(g.slots[i].seq, minseq) < 0) {
                    minseq = g.slots[i].seq;
                    first = false;
                }
            }
        }
        if (cnt >= JITTER_PREBUFFER) {
            g.jb_started = true;
            g.jb_expected = minseq;
            g.jb_missing_polls = 0;
        }
    }
}

/* Fill `out` with the next 320 samples.  Returns true if real audio, false if
 * concealed/silence.  When not yet started, outputs silence and returns false. */
static bool jb_pop(int16_t *out)
{
    if (!g.jb_started) {
        memset(out, 0, sizeof(int16_t) * FRAME_SAMPLES);
        return false;
    }
    jb_slot_t *s = &g.slots[g.jb_expected % JB_CAP];
    bool real = false;
    if (s->present && s->seq == g.jb_expected) {
        memcpy(out, s->pcm, sizeof(int16_t) * FRAME_SAMPLES);
        s->present = false;
        memcpy(g.jb_last, out, sizeof(int16_t) * FRAME_SAMPLES);
        g.jb_have_last = true;
        g.jb_missing_polls = 0;
        real = true;
        g.jb_expected++;
    } else {
        /* Do not advance immediately on one missing frame. A normal Wi-Fi or
         * task-scheduling delay used to make all later frames look late and
         * produced permanent silence. If a future frame is already present,
         * resynchronise to it; otherwise wait up to 80 ms before declaring a
         * loss. This matches the companion's proven receiver behavior. */
        bool have_future = false;
        uint16_t next = 0;
        for (int i = 0; i < JB_CAP; ++i) {
            if (!g.slots[i].present) continue;
            if (jb_seq_delta(g.slots[i].seq, g.jb_expected) > 0 &&
                (!have_future || jb_seq_delta(g.slots[i].seq, next) < 0)) {
                next = g.slots[i].seq;
                have_future = true;
            }
        }
        if (have_future) {
            g.jb_expected = next;
            g.jb_missing_polls = 0;
        } else if (++g.jb_missing_polls >= 4) {
            g.jb_expected++;
            g.jb_missing_polls = 0;
        }
    }
    if (!real && g.jb_have_last) {
        /* packet-loss concealment: replay previous frame at half amplitude */
        for (int i = 0; i < FRAME_SAMPLES; ++i)
            g.jb_last[i] = (int16_t)(g.jb_last[i] / 2);
        memcpy(out, g.jb_last, sizeof(int16_t) * FRAME_SAMPLES);
    } else if (!real) {
        memset(out, 0, sizeof(int16_t) * FRAME_SAMPLES);
    }

    /* drop anything now hopelessly late */
    for (int i = 0; i < JB_CAP; ++i) {
        if (g.slots[i].present &&
            jb_seq_delta(g.jb_expected, g.slots[i].seq) > REORDER_WINDOW)
            g.slots[i].present = false;
    }
    return real;
}

/* ======================================================================== */
/* receive-side floor control (call with g_lock held)                        */
/* ======================================================================== */
static void begin_receiving(const intercom_pkt_t *p)
{
    g.rx_directed = (p->flags & PROTO_FLAG_DIRECTED) != 0;
    g.rx_sender = p->sender_id;
    g.rx_session = p->session_id;
    g.rx_last_ms = now_ms();
    g.rx_ending = false;
    g.rx_drain = 0;
    jb_reset();
    g.state = ST_RECEIVING;
    ESP_LOGI(TAG, "RX start: sender=%08x session=%08x",
             (unsigned)p->sender_id, (unsigned)p->session_id);
}

/* Call with g_lock held. Keep the most recently seen endpoints, replacing the
 * oldest entry if the small fixed registry is full. */
static void peer_seen(const intercom_pkt_t *p, const struct sockaddr_in *source)
{
    int free_slot = -1;
    int oldest = 0;
    for (int i = 0; i < PEER_CAP; ++i) {
        if (g.peers[i].id == p->sender_id) {
            g.peers[i].address = *source;
            g.peers[i].last_seen_ms = now_ms();
            if (p->type == PKT_HELLO) {
                g.peers[i].protocol_version = p->payload[3];
                g.peers[i].capabilities = p->payload[4];
            }
            if (g.have_last_talker && g.last_talker_id == p->sender_id)
                g.last_talker_address = *source;
            return;
        }
        if (g.peers[i].id == 0 && free_slot < 0) free_slot = i;
        if (g.peers[i].last_seen_ms < g.peers[oldest].last_seen_ms) oldest = i;
    }
    int slot = free_slot >= 0 ? free_slot : oldest;
    g.peers[slot].id = p->sender_id;
    g.peers[slot].address = *source;
    g.peers[slot].last_seen_ms = now_ms();
    g.peers[slot].protocol_version = INTERCOM_PROTOCOL_VERSION;
    g.peers[slot].capabilities = p->type == PKT_HELLO ? p->payload[4] : 0;
    ESP_LOGI(TAG, "Peer %08x discovered", (unsigned)p->sender_id);
}

/* g_lock held. IDs remain stable while endpoints may change on rediscovery.
 * Future assistant callers pass g_config.assistant_service_id and SERVICE. */
static const peer_t *find_active_peer_locked(uint32_t id, uint8_t required_capabilities)
{
    for (int i = 0; i < PEER_CAP; ++i) {
        const peer_t *peer = &g.peers[i];
        if (id != 0 && peer->id == id && peer->protocol_version == INTERCOM_PROTOCOL_VERSION &&
            now_ms() - peer->last_seen_ms <= PEER_EXPIRE_MS &&
            (peer->capabilities & required_capabilities) == required_capabilities) return peer;
    }
    return NULL;
}

static void handle_claim(const intercom_pkt_t *p, const struct sockaddr_in *source)
{
    if (g.state == ST_RECEIVING && !g.rx_ending && now_ms() - g.rx_last_ms > RX_TIMEOUT_MS) {
        g.state = ST_IDLE;
        jb_reset();
    }
    bool local_tx = g.state == ST_CLAIMING || g.state == ST_TALKING;
    if (p->flags & PROTO_FLAG_DIRECTED) {
        if (!directed_accept(local_tx, g.state == ST_RECEIVING, g.rx_directed,
                             g.rx_sender, g.rx_session, p->sender_id, p->session_id)) {
            send_packet_to(source, PKT_BUSY, PROTO_FLAG_DIRECTED, p->session_id, 0, NULL, 0);
            return;
        }
        if (g.state != ST_RECEIVING || !g.rx_directed ||
            g.rx_sender != p->sender_id || g.rx_session != p->session_id) begin_receiving(p);
        g.rx_address = *source;
        g.rx_last_ms = now_ms();
        send_packet_to(source, PKT_ACCEPT, PROTO_FLAG_DIRECTED, p->session_id, 0, NULL, 0);
        return;
    }
    /* Broadcast ownership is independent of this node's audio resource. */
    if (local_tx && !g.tx_directed && !remote_wins(p)) {
        send_packet_to(source, PKT_BUSY, 0, g.tx_session, 0, NULL, 0);
        return;
    }
    if (!broadcast_claim(&g.broadcast, p->sender_id, p->session_id, now_ms())) {
        send_packet_to(source, PKT_BUSY, 0, g.broadcast.session, 0, NULL, 0);
        return;
    }
    if ((local_tx && g.tx_directed) || (g.state == ST_RECEIVING && g.rx_directed)) return;
    if (g.state != ST_RECEIVING || g.rx_sender != p->sender_id || g.rx_session != p->session_id)
        begin_receiving(p);
    g.rx_address = *source;
}

static void handle_rtp(const struct sockaddr_in *source, uint32_t session, uint16_t sequence,
                       const uint8_t *payload, uint16_t payload_len)
{
    const peer_t *owner = find_active_peer_locked(g.broadcast.sender, 0);
    if (g.broadcast.active && session == g.broadcast.session && owner &&
        source->sin_addr.s_addr == owner->address.sin_addr.s_addr)
        g.broadcast.last_ms = now_ms();
    if (g.state != ST_RECEIVING || session != g.rx_session ||
        source->sin_addr.s_addr != g.rx_address.sin_addr.s_addr) return;
    g.rx_last_ms = now_ms();
    static int16_t pcm[FRAME_SAMPLES];
    if (adpcm_decode_frame(payload, payload_len, pcm) == 0)
        jb_push(sequence, pcm);
}

static void handle_end(const intercom_pkt_t *p)
{
    if (!(p->flags & PROTO_FLAG_DIRECTED) && broadcast_matches(&g.broadcast, p->sender_id, p->session_id))
        g.broadcast.active = false;
    if (g.state == ST_RECEIVING && p->session_id == g.rx_session && p->sender_id == g.rx_sender &&
        ((p->flags & PROTO_FLAG_DIRECTED) != 0) == g.rx_directed && !g.rx_ending) {
        ESP_LOGI(TAG, "RX end: session=%08x", (unsigned)p->session_id);
        /* Drain whatever is still buffered before returning to idle so the
         * tail of the message is not truncated. */
        g.rx_ending = true;
        g.rx_drain = JITTER_PREBUFFER + REORDER_WINDOW;
    }
}

static void handle_heartbeat(const intercom_pkt_t *p)
{
    if (!(p->flags & PROTO_FLAG_DIRECTED) && broadcast_matches(&g.broadcast, p->sender_id, p->session_id))
        g.broadcast.last_ms = now_ms();
    if (g.state == ST_RECEIVING && p->session_id == g.rx_session && p->sender_id == g.rx_sender &&
        ((p->flags & PROTO_FLAG_DIRECTED) != 0) == g.rx_directed)
        g.rx_last_ms = now_ms();
}

/* USB and remote configuration share a serialised NVS transaction. */
static bool handle_config_packet(const intercom_pkt_t *p,
                                 const struct sockaddr_in *source)
{
    if (p->payload_len == 0 || p->payload_len >= 768) return false;
    char request[768] = {0};
    char response[768] = {0};
    memcpy(request, p->payload, p->payload_len);
    bool restart_required = false;
    usb_control_request(request, response, sizeof(response), &restart_required);
    send_packet_to(source, PKT_CONFIG_REPLY, 0, p->session_id, 0,
                   (const uint8_t *)response, (uint16_t)strlen(response));
    return restart_required;
}

/* A direct HELLO acknowledgement makes discovery work on access points that
 * forward an app's outbound multicast to devices but do not forward device
 * multicast back to a Windows Wi-Fi client (IGMP/multicast isolation). */
static uint16_t build_hello_payload(uint8_t *out, size_t capacity)
{
    uint8_t flags = 0;
    if (button_pressed(MUTE_SWITCH_GPIO)) flags |= 0x01u;
    if (g_config.soft_mute) flags |= 0x02u;
    if (g.state == ST_CLAIMING || g.state == ST_TALKING) flags |= 0x04u;
    return (uint16_t)protocol_hello_pack(out, capacity, INTERCOM_CAPABILITIES, flags,
                                       esp_app_get_description()->version, g_config.alias);
}

static void reply_hello(const struct sockaddr_in *destination)
{
    uint8_t payload[7 + 31 + DEVICE_ALIAS_MAX];
    uint16_t length = build_hello_payload(payload, sizeof(payload));
    send_packet_to(destination, PKT_HELLO, 0, 0, 0, payload, length);
}

/* ======================================================================== */
/* network receive task                                                      */
/* ======================================================================== */
static void net_rx_task(void *arg)
{
    uint8_t buf[PROTO_HEADER_LEN + 768];
    while (1) {
        struct sockaddr_in src;
        socklen_t slen = sizeof(src);
        int n = recvfrom(g_sock, buf, sizeof(buf), 0,
                         (struct sockaddr *)&src, &slen);
        if (n <= 0) { vTaskDelay(pdMS_TO_TICKS(2)); continue; }

        intercom_pkt_t pkt;
        if (!protocol_parse(buf, n, MESH_ID, NODE_ID, &pkt)) continue;

        intercom_hello_t hello;
        if (pkt.type == PKT_HELLO && (!protocol_hello_parse(pkt.payload, pkt.payload_len, &hello) ||
            hello.version != INTERCOM_PROTOCOL_VERSION)) continue;
        bool restart_required = false;
        bool config_request = false;
        const char *ota_reject_message = NULL;
        xSemaphoreTake(g_lock, portMAX_DELAY);
        peer_seen(&pkt, &src);
        if (ota_manager_is_active() && pkt.type == PKT_CLAIM) {
            send_packet_to(&src, PKT_BUSY, pkt.flags & PROTO_FLAG_DIRECTED,
                           (pkt.flags & PROTO_FLAG_DIRECTED) ? pkt.session_id : 0, 0, NULL, 0);
            xSemaphoreGive(g_lock);
            continue;
        }
        switch (pkt.type) {
            case PKT_HELLO: reply_hello(&src); break;
            case PKT_CLAIM: handle_claim(&pkt, &src); break;
            case PKT_END:   handle_end(&pkt);   break;
            case PKT_HEARTBEAT: handle_heartbeat(&pkt); break;
            case PKT_CONFIG_GET:
            case PKT_CONFIG_SET:
                /* Parsing, the NVS commit and the reply run after g_lock is
                 * released; see handle_config_packet. */
                config_request = true;
                break;
            case PKT_OTA_OFFER: {
                /* Do not interrupt speech. The companion can queue and retry
                 * its small, idempotent offer when the floor becomes idle.
                 * This idle test is only a fast pre-filter: the authoritative
                 * floor claim happens on ota_task once the offer's signature
                 * has verified, under g_lock (see ota_claim_floor), so an
                 * unauthenticated datagram never gates PTT. */
                bool idle = g.state == ST_IDLE;
                const char *reason = NULL;
                bool accepted = idle && ota_manager_offer(pkt.payload, pkt.payload_len,
                                                          &src, pkt.session_id, &reason);
                if (!accepted) {
                    ota_reject_message = idle ? (reason ? reason : "OTA rejected")
                                              : "Floor is active";
                    g.error_until_ms = now_ms() + 1500;
                }
                break;
            }
            case PKT_OTA_CANCEL:
                /* Cancellation is deliberately unsupported after acceptance:
                 * stopping a flash write is less safe than letting it finish
                 * or using automatic rollback on the next boot. */
                ota_reject_message = "Cancellation unavailable";
                break;
            case PKT_ACCEPT:
                if (g.state == ST_CLAIMING && g.tx_directed &&
                    (pkt.flags & PROTO_FLAG_DIRECTED) && pkt.session_id == g.tx_session && pkt.sender_id == g.tx_target_id &&
                    src.sin_addr.s_addr == g.tx_audio_destination.sin_addr.s_addr)
                    g.tx_accepted = true;
                break;
            case PKT_BUSY:
                if (g.state == ST_CLAIMING && g.claims_sent > 0 &&
                    (((pkt.flags & PROTO_FLAG_DIRECTED) && g.tx_directed &&
                      pkt.session_id == g.tx_session && pkt.sender_id == g.tx_target_id && src.sin_addr.s_addr == g.tx_audio_destination.sin_addr.s_addr) ||
                     (!(pkt.flags & PROTO_FLAG_DIRECTED) && !g.tx_directed &&
                      (pkt.session_id == 0 || remote_wins(&pkt))))) {
                    g.state = ST_IDLE;
                    g.error_until_ms = now_ms() + 750;
                }
                break;
            default: break;
        }
        if (pkt.type == PKT_CLAIM && g.state == ST_RECEIVING &&
            pkt.session_id == g.rx_session && pkt.sender_id == g.rx_sender &&
            ((pkt.flags & PROTO_FLAG_DIRECTED) != 0) == g.rx_directed) {
            g.last_talker_id = pkt.sender_id;
            g.last_talker_address = src;
            g.have_last_talker = true;
        }
        xSemaphoreGive(g_lock);

        /* Status replies use a blocking sendto and configuration handling
         * commits NVS, so both must run after g_lock is released: the audio
         * tasks block on the lock with portMAX_DELAY every ~20 ms. */
        if (ota_reject_message)
            ota_status_send(&src, pkt.session_id, "rejected", 0, ota_reject_message);
        if (config_request)
            restart_required = handle_config_packet(&pkt, &src);

        if (restart_required) {
            ESP_LOGI(TAG, "Configuration change requires restart");
            vTaskDelay(pdMS_TO_TICKS(300));
            esp_restart();
        }
    }
}

static void rtp_rx_task(void *arg)
{
    uint8_t buf[12 + ADPCM_PAYLOAD_LEN];
    while (1) {
        struct sockaddr_in source;
        socklen_t source_len = sizeof(source);
        int n = recvfrom(g_rtp_sock, buf, sizeof(buf), 0,
                         (struct sockaddr *)&source, &source_len);
        if (n != 12 + ADPCM_PAYLOAD_LEN || (buf[0] >> 6) != 2 ||
            (buf[1] & 0x7f) != RTP_PAYLOAD_TYPE_ADPCM)
            continue;
        uint16_t sequence = ((uint16_t)buf[2] << 8) | buf[3];
        uint32_t session = ((uint32_t)buf[8] << 24) | ((uint32_t)buf[9] << 16) |
                           ((uint32_t)buf[10] << 8) | buf[11];
        xSemaphoreTake(g_lock, portMAX_DELAY);
        handle_rtp(&source, session, sequence, buf + 12, (uint16_t)(n - 12));
        xSemaphoreGive(g_lock);
    }
}

/* ======================================================================== */
/* transmit / PTT / floor-control task                                       */
/* ======================================================================== */
static bool button_pressed(gpio_num_t pin)
{
    return gpio_get_level(pin) == 0;       /* controls are active low */
}

static void start_tx_locked(uint32_t t, bool directed,
                            const struct sockaddr_in *destination)
{
    g.tx_session = esp_random();
    g.tx_sequence = 0;
    g.tx_last_heartbeat_ms = 0;
    g.tx_rtp_sequence = (uint16_t)esp_random();
    g.tx_rtp_timestamp = esp_random();
    g.tx_directed = directed;
    g.tx_accepted = false;
    g.tx_target_id = directed ? g.last_talker_id : 0;
    jb_reset();
    if (destination) g.tx_audio_destination = *destination;
    adpcm_state_reset(&g_adpcm_encoder);
    g.claim_start_ms = t;
    g.last_claim_ms = 0;
    g.claims_sent = 0;
    g.state = ST_CLAIMING;
    ESP_LOGI(TAG, "TX claim: session=%08x%s", (unsigned)g.tx_session,
             directed ? " directed" : " broadcast");
}

static void finish_tx(uint32_t session, uint32_t sequence, bool directed)
{
    for (int i = 0; i < END_COUNT; ++i) {
        if (directed)
            send_packet_to(&g.tx_audio_destination, PKT_END, PROTO_FLAG_DIRECTED, session, sequence, NULL, 0);
        else send_to_active_peers(PKT_END, 0, session, sequence, NULL, 0);
        vTaskDelay(pdMS_TO_TICKS(5));
    }
}

static void tx_task(void *arg)
{
    bool previous_broadcast = false, previous_reply = false;
    uint32_t last_edge_ms = 0;
    audio_frame_t frame;

    while (1) {
        if (ota_manager_is_active()) {
            /* OTA accepts only while idle. Suppress fresh PTT attempts and
             * keep draining the capture queue so no stale speech starts once
             * the update finishes or fails. */
            xQueueReceive(mic_q, &frame, 0);
            vTaskDelay(pdMS_TO_TICKS(10));
            continue;
        }
        uint32_t t = now_ms();
        bool d10_pressed = button_pressed(BUTTON_BROADCAST_GPIO);
        bool d9_pressed = button_pressed(BUTTON_REPLY_GPIO);
        /* g_config is rewritten wholesale by the configuration path under
         * g_lock, so take a snapshot instead of reading it unsynchronised. */
        xSemaphoreTake(g_lock, portMAX_DELAY);
        bool swapped = (g_config.hardware_flags & DEVICE_FLAG_BUTTONS_SWAPPED) != 0;
        xSemaphoreGive(g_lock);
        bool broadcast = swapped ? d9_pressed : d10_pressed;
        bool reply = swapped ? d10_pressed : d9_pressed;
        bool any_pressed = broadcast || reply;

        if ((broadcast != previous_broadcast || reply != previous_reply) &&
            t - last_edge_ms > 20) {
            last_edge_ms = t;
            bool new_press = any_pressed && !(previous_broadcast || previous_reply);
            bool released = !any_pressed && (previous_broadcast || previous_reply);
            previous_broadcast = broadcast;
            previous_reply = reply;

            xSemaphoreTake(g_lock, portMAX_DELAY);
            broadcast_expire(&g.broadcast, t);
            if (new_press) {
                bool directed = reply && !broadcast;
                ESP_LOGI(TAG, "PTT button: %s", directed ? "reply" : "broadcast");
                const peer_t *reply_peer = directed ? find_active_peer_locked(g.last_talker_id, 0) : NULL;
                if (reply_peer) g.last_talker_address = reply_peer->address;
                if (directed && (!g.have_last_talker || !reply_peer)) {
                    /* A reply has no meaning until an incoming AUDIO packet
                     * establishes the last talker. Make that visible rather
                     * than failing silently, and log it for wiring checks. */
                    g.error_until_ms = t + 750;
                    ESP_LOGW(TAG, "Reply unavailable: no previous talker");
                } else if ((g.state == ST_IDLE || g.state == ST_RECEIVING) &&
                           (directed || !g.broadcast.active) && !ota_manager_is_active()) {
                    /* The OTA slot is claimed under g_lock as well, so this
                     * pair of checks cannot both succeed. */
                    g.tx_buffer_count = 0;
                    g.tx_buffer_index = 0;
                    start_tx_locked(t, directed,
                                    directed ? &g.last_talker_address : NULL);
                } else if (g.state == ST_RECEIVING || g.state == ST_IDLE) {
                    g.waiting_for_floor = true;
                    g.waiting_started_ms = t;
                    g.waiting_count = 0;
                    g.waiting_directed = directed;
                    if (directed) g.waiting_destination = g.last_talker_address;
                    ESP_LOGI(TAG, "PTT waiting for occupied floor");
                }
            } else if (released) {
                if (g.waiting_for_floor) {
                    g.waiting_for_floor = false;
                    g.waiting_count = 0;
                } else if (g.state == ST_CLAIMING || g.state == ST_TALKING) {
                    uint32_t session = g.tx_session, sequence = g.tx_sequence;
                    bool directed = g.tx_directed;
                    g.state = ST_IDLE;
                    xSemaphoreGive(g_lock);
                    finish_tx(session, sequence, directed);
                    ESP_LOGI(TAG, "TX end");
                    xSemaphoreTake(g_lock, portMAX_DELAY);
                }
            }
            xSemaphoreGive(g_lock);
        }

        xSemaphoreTake(g_lock, portMAX_DELAY);
        broadcast_expire(&g.broadcast, t);
        if (g.waiting_for_floor) {
            if (!g.broadcast.active && t - g.waiting_started_ms < BUSY_BUFFER_MS && !ota_manager_is_active()) {
                g.tx_buffer_count = g.waiting_count;
                g.tx_buffer_index = 0;
                g.waiting_for_floor = false;
                start_tx_locked(t, g.waiting_directed,
                                g.waiting_directed ? &g.waiting_destination : NULL);
            } else if (t - g.waiting_started_ms >= BUSY_BUFFER_MS) {
                g.waiting_for_floor = false;
                g.waiting_count = 0;
                g.error_until_ms = t + 750;
                xSemaphoreGive(g_lock);
                vTaskDelay(pdMS_TO_TICKS(10));
                continue;
            }
        }
        if (g.state == ST_CLAIMING) {
            if (g.claims_sent < CLAIM_COUNT &&
                (g.last_claim_ms == 0 || t - g.last_claim_ms >= CLAIM_INTERVAL_MS)) {
                uint32_t session = g.tx_session;
                g.last_claim_ms = t;
                g.claims_sent++;
                xSemaphoreGive(g_lock);
                send_packet(PKT_CLAIM, session, 0, NULL, 0);
                xSemaphoreTake(g_lock, portMAX_DELAY);
            }
            if (t - g.claim_start_ms >= PRE_AUDIO_DELAY_MS && g.state == ST_CLAIMING) {
                g.state = (!g.tx_directed || g.tx_accepted) ? ST_TALKING : ST_IDLE;
                if (g.state == ST_IDLE) g.error_until_ms = t + 750;
                g.tx_sequence = 0;
                ESP_LOGI(TAG, "TX talking");
            }
        }
        /* The ADC is the media clock: it delivers one fully formed frame per
         * 20 ms. Do not rate-limit here. The previous timer gate drained and
         * discarded frames between send slots, which produced a time-compressed
         * stream even after replacing Opus with cheap IMA ADPCM. */
        bool can_send = g.state == ST_TALKING;
        bool send_heartbeat = g.state == ST_TALKING &&
                              t - g.tx_last_heartbeat_ms >= 100;
        uint32_t heartbeat_session = g.tx_session;
        if (send_heartbeat) g.tx_last_heartbeat_ms = t;
        xSemaphoreGive(g_lock);

        if (send_heartbeat)
            send_packet(PKT_HEARTBEAT, heartbeat_session, 0, NULL, 0);

        if (!can_send) {
            if (xQueueReceive(mic_q, &frame, 0) == pdTRUE) {
                xSemaphoreTake(g_lock, portMAX_DELAY);
                if (g.waiting_for_floor && g.waiting_count < BUSY_BUFFER_FRAMES)
                    g.waiting_frames[g.waiting_count++] = frame;
                xSemaphoreGive(g_lock);
            }
            vTaskDelay(pdMS_TO_TICKS(2));
            continue;
        }

        bool have_frame = false;
        xSemaphoreTake(g_lock, portMAX_DELAY);
        if (g.tx_buffer_index < g.tx_buffer_count) {
            frame = g.waiting_frames[g.tx_buffer_index++];
            have_frame = true;
        }
        xSemaphoreGive(g_lock);
        if (!have_frame)
            have_frame = xQueueReceive(mic_q, &frame, 0) == pdTRUE;
        if (!have_frame) { vTaskDelay(pdMS_TO_TICKS(1)); continue; }

        xSemaphoreTake(g_lock, portMAX_DELAY);
        if (g.state == ST_TALKING) {
            uint8_t payload[ADPCM_PAYLOAD_LEN];
            uint32_t session = g.tx_session;
            uint16_t rtp_sequence = g.tx_rtp_sequence++;
            uint32_t rtp_timestamp = g.tx_rtp_timestamp;
            g.tx_rtp_timestamp += RTP_TIMESTAMP_STEP;
            g.tx_sequence++;
            bool directed = g.tx_directed;
            struct sockaddr_in destination = g.tx_audio_destination;
            xSemaphoreGive(g_lock);
            adpcm_encode_frame(&g_adpcm_encoder, frame.pcm, payload);
            if (directed)
                send_rtp_to(&destination, session, rtp_sequence, rtp_timestamp,
                            payload, sizeof(payload));
            else
                send_rtp_to_active_peers(session, rtp_sequence, rtp_timestamp,
                                         payload, sizeof(payload));
        } else if (g.waiting_for_floor && g.waiting_count < BUSY_BUFFER_FRAMES) {
            g.waiting_frames[g.waiting_count++] = frame;
            xSemaphoreGive(g_lock);
        } else {
            xSemaphoreGive(g_lock);
        }
    }
}

/* ======================================================================== */
/* playback task: jitter buffer -> I2S, muted while we hold the floor        */
/* ======================================================================== */
static void write_i2s(const int16_t *mono)
{
    static int16_t stereo[FRAME_SAMPLES * 2];
    for (int i = 0; i < FRAME_SAMPLES; ++i) {
        stereo[2 * i]     = mono[i];   /* duplicate mono into L and R slots */
        stereo[2 * i + 1] = mono[i];
    }
    size_t written = 0;
    i2s_channel_write(i2s_tx, stereo, sizeof(stereo), &written,
                      pdMS_TO_TICKS(60));
}

static void playback_task(void *arg)
{
    static int16_t mono[FRAME_SAMPLES];
    bool i2s_active = false;
    TickType_t next_tick = xTaskGetTickCount();
    while (1) {
        /* i2s_channel_write can accept several DMA frames immediately. The
         * jitter buffer itself must therefore enforce the 20 ms media clock;
         * otherwise its expected sequence races ahead of the UDP stream. */
        vTaskDelayUntil(&next_tick, pdMS_TO_TICKS(20));
        xSemaphoreTake(g_lock, portMAX_DELAY);
        node_state_t st = g.state;
        bool local_floor = (st == ST_CLAIMING || st == ST_TALKING);
        /* Snapshot the configuration fields used below: the configuration path
         * replaces the whole struct under g_lock. */
        bool soft_muted = g_config.soft_mute != 0;
        uint16_t volume = g_config.speaker_volume;

        if (st == ST_RECEIVING) {
            uint32_t age = now_ms() - g.rx_last_ms;
            if (!g.rx_ending && age > RX_TIMEOUT_MS) {   /* heartbeat lost */
                ESP_LOGI(TAG, "RX timeout, releasing floor");
                g.state = ST_IDLE;
                jb_reset();
                st = ST_IDLE;
            }
        }

        bool real_audio = false;
        if (st == ST_RECEIVING) {
            real_audio = jb_pop(mono);
            if (g.rx_ending && --g.rx_drain <= 0) {   /* tail drained */
                g.state = ST_IDLE;
                jb_reset();
            }
        } else {
            memset(mono, 0, sizeof(mono));
        }
        xSemaphoreGive(g_lock);

        /* Keep the amplifier in standby outside actual received playback.
         * MAX98357A enters high-impedance standby when BCLK is stopped. */
        bool output_allowed = !local_floor && !button_pressed(MUTE_SWITCH_GPIO) && !soft_muted &&
                              st == ST_RECEIVING;
        if (!output_allowed)
            memset(mono, 0, sizeof(mono));

        /* Once a receive session has real audio, keep the I2S clocks running
         * through its jitter-buffer tail for seamless packet-loss concealment. */
        bool should_output = output_allowed && (i2s_active || real_audio);
        if (should_output && !i2s_active) {
            ESP_ERROR_CHECK(i2s_channel_enable(i2s_tx));
            i2s_active = true;
        } else if (!should_output && i2s_active) {
            ESP_ERROR_CHECK(i2s_channel_disable(i2s_tx));
            i2s_active = false;
        }
        if (!should_output) continue;

        /* NVS-configured digital playback volume (/256) with safe clipping. */
        for (int i = 0; i < FRAME_SAMPLES; ++i) {
            int32_t v = ((int32_t)mono[i] * volume) >> 8;
            if (v > 32767)  v = 32767;
            if (v < -32768) v = -32768;
            mono[i] = (int16_t)v;
        }

        write_i2s(mono);   /* paces the loop at ~20 ms (320 samples @16kHz) */
    }
}

/* ======================================================================== */
/* audio capture: ADC continuous @16kHz -> conditioned 320-sample frames     */
/* ======================================================================== */
static void capture_task(void *arg)
{
    uint8_t buf[512];
    audio_frame_t frame;
    int fill = 0;
    int32_t dc = 0;         /* DC estimate in (raw << 8) fixed point */
    bool dc_init = false;

    /* --- diagnostics: once/sec report of the raw ADC and AC signal --- */
    int32_t dmin = 4095, dmax = 0;         /* raw ADC span   */
    int64_t dsum = 0;                      /* raw ADC mean   */
    int32_t acpk = 0;                      /* |centered| peak */
    uint32_t dcount = 0;
    bool warned_nomatch = false;
    int consecutive_timeouts = 0;

    while (1) {
        bool matched_any = false;
        uint32_t got = 0;
        esp_err_t read_err = adc_continuous_read(adc_handle, buf, sizeof(buf), &got,
                                                 pdMS_TO_TICKS(500));
        if (read_err == ESP_ERR_TIMEOUT) {
            if (++consecutive_timeouts >= 3) {
                /* Flash writes temporarily pause the ADC DMA path on the C3.
                 * Calling adc_continuous_stop/start from this recovery path
                 * races the driver's DMA mutex and can assert inside
                 * xTaskPriorityDisinherit. There is no useful microphone data
                 * while an OTA writes flash anyway, so leave the driver armed
                 * and let it resume as soon as the flash operation ends. */
                ESP_LOGW(TAG, "MIC diag: ADC stream paused; leaving capture armed");
                consecutive_timeouts = 0;
            }
            continue;
        }
        if (read_err != ESP_OK) {
            ESP_LOGW(TAG, "MIC diag: ADC read failed: %s", esp_err_to_name(read_err));
            vTaskDelay(pdMS_TO_TICKS(20));
            continue;
        }
        consecutive_timeouts = 0;

        for (uint32_t i = 0; i + SOC_ADC_DIGI_RESULT_BYTES <= got;
             i += SOC_ADC_DIGI_RESULT_BYTES) {
            adc_digi_output_data_t *p = (adc_digi_output_data_t *)&buf[i];
            uint32_t ch  = p->type2.channel;
            int32_t  raw = p->type2.data;
            if (ch != MIC_ADC_CHANNEL) continue;
            matched_any = true;

            /* running-average DC removal (high-pass) */
            int32_t rawq = raw << 8;
            if (!dc_init) { dc = rawq; dc_init = true; }
            dc += (rawq - dc) >> 6;
            int32_t centered = raw - (dc >> 8);

            /* conservative software gain + safe clip */
            int32_t s = centered * MIC_GAIN;
            if (s > 32767)  s = 32767;
            if (s < -32768) s = -32768;

            /* accumulate diagnostics */
            if (raw < dmin) dmin = raw;
            if (raw > dmax) dmax = raw;
            dsum += raw;
            int32_t a = centered < 0 ? -centered : centered;
            if (a > acpk) acpk = a;
            if (++dcount >= SAMPLE_RATE) {
                ESP_LOGI(TAG,
                    "MIC diag: raw[min=%d max=%d mean=%d span=%d] ac_peak=%d gain=%d",
                    (int)dmin, (int)dmax, (int)(dsum / (int64_t)dcount),
                    (int)(dmax - dmin), (int)acpk, MIC_GAIN);
                dmin = 4095; dmax = 0; dsum = 0; acpk = 0; dcount = 0;
            }

            frame.pcm[fill++] = (int16_t)s;
            if (fill == FRAME_SAMPLES) {
                fill = 0;
#if INTERCOM_USB_RAW_MIC_CAPTURE
                if (raw_mic_q) xQueueSend(raw_mic_q, &frame, 0);
#endif
                xQueueSend(mic_q, &frame, 0);   /* drop if full: stay realtime */
            }
        }
        if (!matched_any && !warned_nomatch) {
            warned_nomatch = true;
            ESP_LOGW(TAG, "MIC diag: no ADC results on channel %d "
                          "(wrong channel/format?)", (int)MIC_ADC_CHANNEL);
        }
    }
}

/* ======================================================================== */
/* peripheral init                                                           */
/* ======================================================================== */
static void i2s_init(void)
{
    i2s_chan_config_t chan_cfg =
        I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_AUTO, I2S_ROLE_MASTER);
    ESP_ERROR_CHECK(i2s_new_channel(&chan_cfg, &i2s_tx, NULL));

    i2s_std_config_t std_cfg = {
        .clk_cfg  = I2S_STD_CLK_DEFAULT_CONFIG(SAMPLE_RATE),
        .slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(
            I2S_DATA_BIT_WIDTH_16BIT, I2S_SLOT_MODE_STEREO),
        .gpio_cfg = {
            .mclk = I2S_GPIO_UNUSED,
            .bclk = I2S_BCLK_GPIO,
            .ws   = I2S_LRC_GPIO,
            .dout = I2S_DOUT_GPIO,
            .din  = I2S_GPIO_UNUSED,
            .invert_flags = { 0 },
        },
    };
    ESP_ERROR_CHECK(i2s_channel_init_std_mode(i2s_tx, &std_cfg));
    /* BCLK remains stopped at idle, putting the MAX98357A into standby. */
}

static void adc_init(void)
{
    adc_continuous_handle_cfg_t handle_cfg = {
        .max_store_buf_size = 2048,
        .conv_frame_size = 256,
    };
    ESP_ERROR_CHECK(adc_continuous_new_handle(&handle_cfg, &adc_handle));

    adc_digi_pattern_config_t pattern = {
        .atten = ADC_ATTEN_DB_12,       /* full-scale ~0..3.3V input range */
        .channel = MIC_ADC_CHANNEL,
        .unit = ADC_UNIT_1,
        .bit_width = ADC_BITWIDTH_12,
    };
    adc_continuous_config_t dig_cfg = {
        .pattern_num = 1,
        .adc_pattern = &pattern,
        .sample_freq_hz = SAMPLE_RATE,
        .conv_mode = ADC_CONV_SINGLE_UNIT_1,
        .format = ADC_DIGI_OUTPUT_FORMAT_TYPE2,
    };
    ESP_ERROR_CHECK(adc_continuous_config(adc_handle, &dig_cfg));
    ESP_ERROR_CHECK(adc_continuous_start(adc_handle));
}

static void gpio_button_init(void)
{
    gpio_config_t io = {
        .pin_bit_mask = (1ULL << BUTTON_BROADCAST_GPIO) |
                        (1ULL << BUTTON_REPLY_GPIO) |
                        (1ULL << MUTE_SWITCH_GPIO),
        .mode = GPIO_MODE_INPUT,
        .pull_up_en = GPIO_PULLUP_ENABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    ESP_ERROR_CHECK(gpio_config(&io));
}

static void ring_task(void *arg)
{
    while (1) {
        uint32_t t = now_ms();
        bool hardware_muted = button_pressed(MUTE_SWITCH_GPIO);
        bool save_soft_mute_clear = false;
        xSemaphoreTake(g_lock, portMAX_DELAY);
        /* Preserve physical-slider clearing, serialised with remote/USB writes. */
        save_soft_mute_clear = hardware_muted && g_config.soft_mute;
        bool soft_muted = g_config.soft_mute;
        node_state_t state = g.state;
        bool rx_audio_started = g.jb_started;
        bool directed = g.tx_directed;
        bool error = (int32_t)(g.error_until_ms - t) > 0;
        xSemaphoreGive(g_lock);

        if (save_soft_mute_clear) {
            char response[768];
            bool restart_required = false;
            if (usb_control_request("{\"cmd\":\"set\",\"soft_mute\":false}", response,
                                    sizeof(response), &restart_required))
                ESP_LOGI(TAG, "Hardware mute cleared the soft-mute flag");
            else
                ESP_LOGW(TAG, "Could not persist soft-mute clear; will retry");
        }

        if (ota_manager_is_active()) ring_controller_set(RING_OTA);
        else if (ota_manager_has_recent_error()) ring_controller_set(RING_ERROR);
        else if (error) ring_controller_set(RING_ERROR);
        else if (hardware_muted) ring_controller_set(RING_MUTE);
        else if (soft_muted) ring_controller_set(RING_SOFT_MUTE);
        else if (state == ST_TALKING || state == ST_CLAIMING)
            ring_controller_set(directed ? RING_TALK_REPLY : RING_TALK_BROADCAST);
        /* A CLAIM reserves the floor, but Speaking is strictly a playback
         * indication. Configuration/control packets and empty claims must not
         * cause the received-message animation. */
        else if (state == ST_RECEIVING && rx_audio_started) ring_controller_set(RING_SPEAKING);
        else ring_controller_set(RING_IDLE);
        ring_controller_update(t);
        vTaskDelay(pdMS_TO_TICKS(20));
    }
}

static void hello_task(void *arg)
{
    while (1) {
        uint8_t payload[7 + 31 + DEVICE_ALIAS_MAX];
        /* The configuration path replaces the whole g_config struct under
         * g_lock, so build the payload (which copies the alias byte by byte)
         * under the lock too and only send after releasing it. */
        xSemaphoreTake(g_lock, portMAX_DELAY);
        uint16_t length = build_hello_payload(payload, sizeof(payload));
        xSemaphoreGive(g_lock);
        send_packet_to(&g_group, PKT_HELLO, 0, 0, 0, payload, length);
        vTaskDelay(pdMS_TO_TICKS(PEER_HELLO_MS));
    }
}

static void ota_health_task(void *arg)
{
    /* A pending image gets a full control/audio/network startup window before
     * it is committed. A reset before this point is handled by bootloader
     * rollback, preserving the previous application slot. */
    vTaskDelay(pdMS_TO_TICKS(12000));
    ota_manager_mark_running_valid();
    vTaskDelete(NULL);
}

/* ======================================================================== */
/* Wi-Fi                                                                     */
/* ======================================================================== */
static void udp_join_multicast(uint32_t interface_addr)
{
    if (g_sock < 0 || interface_addr == 0) return;
    struct ip_mreq membership = {
        .imr_multiaddr = g_group.sin_addr,
        .imr_interface.s_addr = interface_addr,
    };
    if (setsockopt(g_sock, IPPROTO_IP, IP_ADD_MEMBERSHIP,
                   &membership, sizeof(membership)) == 0)
        ESP_LOGI(TAG, "Joined multicast group %s", MULTICAST_GROUP);
    else
        ESP_LOGW(TAG, "Multicast join failed; will retry after reconnect");
}

static void wifi_event_handler(void *arg, esp_event_base_t base,
                               int32_t id, void *data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        g_wifi_ip_addr = 0;
        ESP_LOGW(TAG, "Wi-Fi disconnected, reconnecting...");
        esp_wifi_connect();
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *e = (ip_event_got_ip_t *)data;
        ESP_LOGI(TAG, "Got IP: " IPSTR, IP2STR(&e->ip_info.ip));
        g_wifi_ip_addr = e->ip_info.ip.addr;
        udp_join_multicast(g_wifi_ip_addr);
    }
}

static void wifi_init(void)
{
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();

    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&cfg));

    ESP_ERROR_CHECK(esp_event_handler_instance_register(
        WIFI_EVENT, ESP_EVENT_ANY_ID, &wifi_event_handler, NULL, NULL));
    ESP_ERROR_CHECK(esp_event_handler_instance_register(
        IP_EVENT, IP_EVENT_STA_GOT_IP, &wifi_event_handler, NULL, NULL));

    wifi_config_t wc = { 0 };
    strncpy((char *)wc.sta.ssid, g_config.wifi_ssid, sizeof(wc.sta.ssid) - 1);
    strncpy((char *)wc.sta.password, g_config.wifi_password, sizeof(wc.sta.password) - 1);

    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wc));
    ESP_ERROR_CHECK(esp_wifi_start());
    /* disable power save for low latency */
    ESP_ERROR_CHECK(esp_wifi_set_ps(WIFI_PS_NONE));
    ESP_LOGI(TAG, "Wi-Fi started, connecting to \"%s\"", g_config.wifi_ssid);
}

static void udp_init(void)
{
    g_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    int reuse = 1;
    setsockopt(g_sock, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
    int tos = INTERCOM_IP_TOS_VIDEO;
    setsockopt(g_sock, IPPROTO_IP, IP_TOS, &tos, sizeof(tos));
    struct sockaddr_in local = {
        .sin_family = AF_INET,
        .sin_port = htons(UDP_PORT),
        .sin_addr.s_addr = htonl(INADDR_ANY),
    };
    ESP_ERROR_CHECK(bind(g_sock, (struct sockaddr *)&local, sizeof(local)) == 0 ? ESP_OK : ESP_FAIL);

    memset(&g_group, 0, sizeof(g_group));
    g_group.sin_family = AF_INET;
    g_group.sin_port = htons(UDP_PORT);
    g_group.sin_addr.s_addr = inet_addr(MULTICAST_GROUP);
    uint8_t ttl = MULTICAST_TTL;
    ESP_ERROR_CHECK(setsockopt(g_sock, IPPROTO_IP, IP_MULTICAST_TTL,
                               &ttl, sizeof(ttl)) == 0 ? ESP_OK : ESP_FAIL);
    ESP_LOGI(TAG, "UDP ready on port %d; waiting for multicast join", UDP_PORT);
    udp_join_multicast(g_wifi_ip_addr);  /* covers an exceptionally fast DHCP lease */

    g_rtp_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    ESP_ERROR_CHECK(g_rtp_sock >= 0 ? ESP_OK : ESP_FAIL);
    setsockopt(g_rtp_sock, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
    setsockopt(g_rtp_sock, IPPROTO_IP, IP_TOS, &tos, sizeof(tos));
    struct sockaddr_in rtp_local = {
        .sin_family = AF_INET,
        .sin_port = htons(RTP_PORT),
        .sin_addr.s_addr = htonl(INADDR_ANY),
    };
    ESP_ERROR_CHECK(bind(g_rtp_sock, (struct sockaddr *)&rtp_local,
                         sizeof(rtp_local)) == 0 ? ESP_OK : ESP_FAIL);
    ESP_LOGI(TAG, "RTP/IMA ADPCM ready on port %d (WMM video priority)", RTP_PORT);
}

/* ======================================================================== */
/* app entry                                                                 */
/* ======================================================================== */
void app_main(void)
{
    esp_err_t nvs = nvs_flash_init();
    if (nvs == ESP_ERR_NVS_NO_FREE_PAGES ||
        nvs == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        nvs = nvs_flash_init();
    }
    ESP_ERROR_CHECK(nvs);

    device_config_load(&g_config);

    g_lock = xSemaphoreCreateMutex();
    mic_q  = xQueueCreate(8, sizeof(audio_frame_t));
    memset(&g, 0, sizeof(g));
    g.state = ST_IDLE;
    jb_reset();
    usb_control_start(&g_config, g_lock);

    i2s_init();
    gpio_button_init();
    ring_controller_init(LED_RING_GPIO, LED_RING_COUNT, g_config.led_brightness,
                         (g_config.hardware_flags & DEVICE_FLAG_RING_180) ? 180 : 0);
    adc_init();
    adpcm_state_reset(&g_adpcm_encoder);

    wifi_init();
    udp_init();
    ota_manager_init(ota_status_send, ota_claim_floor);

#if INTERCOM_USB_RAW_MIC_CAPTURE
    raw_mic_q = xQueueCreate(16, sizeof(audio_frame_t));
    ESP_ERROR_CHECK(raw_mic_q ? ESP_OK : ESP_ERR_NO_MEM);
    xTaskCreate(raw_mic_usb_task, "raw_mic_usb", 4096, NULL, 3, NULL);
    ESP_LOGW(TAG, "DIAGNOSTIC build: streaming conditioned 16 kHz PCM over USB");
#endif

    xTaskCreate(net_rx_task,   "net_rx",   8192, NULL, 6, NULL);
    xTaskCreate(rtp_rx_task,   "rtp_rx",   4096, NULL, 6, NULL);
    xTaskCreate(capture_task,  "capture",  4096, NULL, 6, NULL);
    xTaskCreate(tx_task,       "tx",       4096, NULL, 5, NULL);
    xTaskCreate(playback_task, "playback", 4096, NULL, 5, NULL);
    xTaskCreate(ring_task,     "ring",     4096, NULL, 4, NULL);
    xTaskCreate(hello_task,    "hello",    3072, NULL, 3, NULL);
    xTaskCreate(ota_health_task, "ota_health", 3072, NULL, 2, NULL);

    ESP_LOGI(TAG, "RX jitter: %d-frame capacity / %d-frame prebuffer; free heap=%u bytes",
             JB_CAP, JITTER_PREBUFFER, (unsigned)esp_get_free_heap_size());
    ESP_LOGI(TAG, "Intercom node %08x ready", (unsigned)NODE_ID);
}
