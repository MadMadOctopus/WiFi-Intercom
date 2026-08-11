/*
 * app_main.c - Wi-Fi half-duplex push-to-talk intercom (ESP32-C3 peer).
 *
 * One node of a two-peer intercom.  The other peer is the PC application in
 * ../pc_app.  Both speak the identical UDP protocol (protocol.c) and IMA ADPCM
 * codec (adpcm.c).
 *
 * Tasks (FreeRTOS), decoupled by queues / a shared mutex so Wi-Fi, capture,
 * playback and button handling never block each other:
 *
 *   capture_task   ADC continuous @16kHz -> conditioned 320-sample frames -> mic_q
 *   tx_task        PTT + floor control; drains mic_q, encodes, sends AUDIO
 *   net_rx_task    receives UDP, runs receive-side floor logic, fills jitter buf
 *   playback_task  every 20 ms pops jitter buffer -> I2S (muted while we talk)
 *
 * Pin map (see README / config.h):
 *   Mic OUT  -> GPIO3  (ADC1_CH3)      PTT button -> GPIO20 (INPUT_PULLUP)
 *   I2S BCLK -> GPIO6  LRC -> GPIO7  DOUT -> GPIO5 (MAX98357A)
 */
#include <string.h>
#include <stdlib.h>
#include <math.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"

#include "esp_log.h"
#include "esp_system.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "esp_event.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "nvs_flash.h"

#include "driver/gpio.h"
#include "driver/i2s_std.h"
#include "esp_adc/adc_continuous.h"

#include "lwip/sockets.h"

#include "config.h"
#include "device_config.h"
#include "protocol.h"
#include "adpcm.h"
#include "ring_controller.h"
#include "usb_control.h"

/* The POC's call sites use these values extensively. They now resolve to the
 * NVS-backed production configuration loaded during boot. */
static device_config_t g_config;
#define MESH_ID (g_config.mesh_id)
#define NODE_ID (g_config.device_id)
#define PTT_GPIO BUTTON_BROADCAST_GPIO

static const char *TAG = "intercom";

/* ---- audio frame passed capture -> tx ---------------------------------- */
typedef struct { int16_t pcm[FRAME_SAMPLES]; } audio_frame_t;

/* ---- floor-control states ---------------------------------------------- */
typedef enum { ST_IDLE, ST_CLAIMING, ST_TALKING, ST_RECEIVING } node_state_t;

/* ---- jitter buffer ------------------------------------------------------ */
#define JB_CAP 16
typedef struct {
    int16_t  pcm[FRAME_SAMPLES];
    uint32_t seq;
    bool     present;
} jb_slot_t;

/* ---- shared node state (guarded by g_lock) ----------------------------- */
static struct {
    node_state_t state;

    /* transmit */
    uint32_t tx_session;
    uint32_t tx_sequence;
    uint32_t tx_last_audio_ms;
    uint32_t tx_last_heartbeat_ms;
    int      tx_buffer_index;
    int      tx_buffer_count;
    adpcm_state_t enc;
    uint32_t claim_start_ms;
    int claims_sent;
    uint32_t last_claim_ms;
    bool     tx_directed;
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

    /* jitter buffer */
    jb_slot_t slots[JB_CAP];
    uint32_t  jb_expected;
    bool      jb_started;
    int16_t   jb_last[FRAME_SAMPLES];
    bool      jb_have_last;
} g;

static SemaphoreHandle_t g_lock;
static QueueHandle_t     mic_q;

static int                 g_sock = -1;
static struct sockaddr_in  g_group;

static i2s_chan_handle_t          i2s_tx;
static adc_continuous_handle_t    adc_handle;

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
    uint8_t buf[PROTO_HEADER_LEN + 320];
    size_t n = protocol_pack(buf, type, flags, MESH_ID, NODE_ID, session, sequence,
                             now_ms(), payload, payload_len);
    if (g_sock >= 0)
        sendto(g_sock, buf, n, 0, (const struct sockaddr *)destination,
               sizeof(*destination));
}

static void send_packet(uint8_t type, uint32_t session, uint32_t sequence,
                        const uint8_t *payload, uint16_t payload_len)
{
    uint8_t flags = 0;
    const struct sockaddr_in *destination = &g_group;
    if (type == PKT_AUDIO && g.tx_directed) {
        flags = PROTO_FLAG_DIRECTED;
        destination = &g.tx_audio_destination;
    }
    send_packet_to(destination, type, flags, session, sequence, payload, payload_len);
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
static void jb_reset(void)
{
    for (int i = 0; i < JB_CAP; ++i) g.slots[i].present = false;
    g.jb_expected = 0;
    g.jb_started = false;
    g.jb_have_last = false;
}

static void jb_push(uint32_t seq, const int16_t *pcm)
{
    if (g.jb_started && seq < g.jb_expected) return;      /* too late */
    jb_slot_t *s = &g.slots[seq % JB_CAP];
    if (s->present && s->seq == seq) return;              /* duplicate */
    s->present = true;
    s->seq = seq;
    memcpy(s->pcm, pcm, sizeof(s->pcm));

    if (!g.jb_started) {
        /* count distinct buffered frames; start once we have the prebuffer */
        int cnt = 0;
        uint32_t minseq = 0;
        bool first = true;
        for (int i = 0; i < JB_CAP; ++i) {
            if (g.slots[i].present) {
                cnt++;
                if (first || g.slots[i].seq < minseq) { minseq = g.slots[i].seq; first = false; }
            }
        }
        if (cnt >= JITTER_PREBUFFER) {
            g.jb_started = true;
            g.jb_expected = minseq;
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
        real = true;
    } else if (g.jb_have_last) {
        /* packet-loss concealment: replay previous frame at half amplitude */
        for (int i = 0; i < FRAME_SAMPLES; ++i)
            g.jb_last[i] = (int16_t)(g.jb_last[i] / 2);
        memcpy(out, g.jb_last, sizeof(int16_t) * FRAME_SAMPLES);
    } else {
        memset(out, 0, sizeof(int16_t) * FRAME_SAMPLES);
    }
    g.jb_expected++;

    /* drop anything now hopelessly late */
    for (int i = 0; i < JB_CAP; ++i) {
        if (g.slots[i].present &&
            g.slots[i].seq + REORDER_WINDOW < g.jb_expected)
            g.slots[i].present = false;
    }
    return real;
}

/* ======================================================================== */
/* receive-side floor control (call with g_lock held)                        */
/* ======================================================================== */
static void begin_receiving(const intercom_pkt_t *p)
{
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

static void handle_claim(const intercom_pkt_t *p)
{
    if (g.state == ST_CLAIMING || g.state == ST_TALKING) {
        if (remote_wins(p)) { begin_receiving(p); }
        else { send_packet(PKT_BUSY, g.tx_session, 0, NULL, 0); }
    } else if (g.state == ST_IDLE) {
        begin_receiving(p);
    } else if (g.state == ST_RECEIVING && p->session_id != g.rx_session) {
        send_packet(PKT_BUSY, g.rx_session, 0, NULL, 0);
    }
}

static void handle_audio(const intercom_pkt_t *p)
{
    if (g.state == ST_CLAIMING || g.state == ST_TALKING) {
        if (remote_wins(p)) begin_receiving(p);
        else return;
    }
    if (g.state == ST_IDLE) begin_receiving(p);
    if (g.state != ST_RECEIVING || p->session_id != g.rx_session) return;

    g.rx_last_ms = now_ms();
    static int16_t pcm[FRAME_SAMPLES];
    if (adpcm_decode_frame(p->payload, p->payload_len, pcm) == 0)
        jb_push(p->sequence, pcm);
}

static void handle_end(const intercom_pkt_t *p)
{
    if (g.state == ST_RECEIVING && p->session_id == g.rx_session &&
        !g.rx_ending) {
        ESP_LOGI(TAG, "RX end: session=%08x", (unsigned)p->session_id);
        /* Drain whatever is still buffered before returning to idle so the
         * tail of the message is not truncated. */
        g.rx_ending = true;
        g.rx_drain = JITTER_PREBUFFER + REORDER_WINDOW;
    }
}

static void handle_heartbeat(const intercom_pkt_t *p)
{
    if (g.state == ST_RECEIVING && p->session_id == g.rx_session)
        g.rx_last_ms = now_ms();
}

static void handle_config_packet(const intercom_pkt_t *p,
                                 const struct sockaddr_in *source)
{
    if (p->payload_len == 0 || p->payload_len >= 256) return;
    char request[256] = {0};
    char response[256] = {0};
    memcpy(request, p->payload, p->payload_len);
    bool wifi_changed = false;
    usb_control_process(&g_config, request, response, sizeof(response), &wifi_changed);
    send_packet_to(source, PKT_CONFIG_REPLY, 0, p->session_id, 0,
                   (const uint8_t *)response, (uint16_t)strlen(response));
    if (wifi_changed) {
        ESP_LOGI(TAG, "Wi-Fi configuration changed; restarting");
        vTaskDelay(pdMS_TO_TICKS(300));
        esp_restart();
    }
}

/* ======================================================================== */
/* network receive task                                                      */
/* ======================================================================== */
static void net_rx_task(void *arg)
{
    uint8_t buf[PROTO_HEADER_LEN + 320];
    while (1) {
        struct sockaddr_in src;
        socklen_t slen = sizeof(src);
        int n = recvfrom(g_sock, buf, sizeof(buf), 0,
                         (struct sockaddr *)&src, &slen);
        if (n <= 0) { vTaskDelay(pdMS_TO_TICKS(2)); continue; }

        intercom_pkt_t pkt;
        if (!protocol_parse(buf, n, MESH_ID, NODE_ID, &pkt)) continue;

        xSemaphoreTake(g_lock, portMAX_DELAY);
        switch (pkt.type) {
            case PKT_CLAIM: handle_claim(&pkt); break;
            case PKT_AUDIO: handle_audio(&pkt); break;
            case PKT_END:   handle_end(&pkt);   break;
            case PKT_HEARTBEAT: handle_heartbeat(&pkt); break;
            case PKT_CONFIG_GET:
            case PKT_CONFIG_SET: handle_config_packet(&pkt, &src); break;
            case PKT_BUSY:
                if (g.state == ST_CLAIMING) g.state = ST_IDLE;
                break;
            default: break;
        }
        if (pkt.type == PKT_AUDIO && g.state == ST_RECEIVING &&
            pkt.session_id == g.rx_session) {
            g.last_talker_id = pkt.sender_id;
            g.last_talker_address = src;
            g.have_last_talker = true;
        }
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
    g.tx_last_audio_ms = 0;
    g.tx_last_heartbeat_ms = 0;
    g.tx_directed = directed;
    if (destination) g.tx_audio_destination = *destination;
    adpcm_state_reset(&g.enc);
    g.claim_start_ms = t;
    g.last_claim_ms = 0;
    g.claims_sent = 0;
    g.state = ST_CLAIMING;
    ESP_LOGI(TAG, "TX claim: session=%08x%s", (unsigned)g.tx_session,
             directed ? " directed" : " broadcast");
}

static void finish_tx(uint32_t session, uint32_t sequence, bool directed,
                      const struct sockaddr_in *destination)
{
    for (int i = 0; i < END_COUNT; ++i) {
        send_packet_to(&g_group, PKT_END, directed ? PROTO_FLAG_DIRECTED : 0,
                       session, sequence, NULL, 0);
        if (directed)
            send_packet_to(destination, PKT_END, PROTO_FLAG_DIRECTED,
                           session, sequence, NULL, 0);
        vTaskDelay(pdMS_TO_TICKS(5));
    }
}

static void tx_task(void *arg)
{
    bool previous_broadcast = false, previous_reply = false;
    uint32_t last_edge_ms = 0;
    audio_frame_t frame;

    while (1) {
        uint32_t t = now_ms();
        bool broadcast = button_pressed(BUTTON_BROADCAST_GPIO);
        bool reply = button_pressed(BUTTON_REPLY_GPIO);
        bool any_pressed = broadcast || reply;

        if ((broadcast != previous_broadcast || reply != previous_reply) &&
            t - last_edge_ms > 20) {
            last_edge_ms = t;
            bool new_press = any_pressed && !(previous_broadcast || previous_reply);
            bool released = !any_pressed && (previous_broadcast || previous_reply);
            previous_broadcast = broadcast;
            previous_reply = reply;

            xSemaphoreTake(g_lock, portMAX_DELAY);
            if (new_press) {
                bool directed = reply && !broadcast;
                if (directed && !g.have_last_talker) {
                    ESP_LOGI(TAG, "Reply ignored: no previous talker");
                } else if (g.state == ST_IDLE) {
                    g.tx_buffer_count = 0;
                    g.tx_buffer_index = 0;
                    start_tx_locked(t, directed,
                                    directed ? &g.last_talker_address : NULL);
                } else if (g.state == ST_RECEIVING) {
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
                    struct sockaddr_in destination = g.tx_audio_destination;
                    g.state = ST_IDLE;
                    xSemaphoreGive(g_lock);
                    finish_tx(session, sequence, directed, &destination);
                    ESP_LOGI(TAG, "TX end");
                    xSemaphoreTake(g_lock, portMAX_DELAY);
                }
            }
            xSemaphoreGive(g_lock);
        }

        xSemaphoreTake(g_lock, portMAX_DELAY);
        if (g.waiting_for_floor) {
            if (g.state == ST_IDLE) {
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
                uint8_t flags = g.tx_directed ? PROTO_FLAG_DIRECTED : 0;
                g.last_claim_ms = t;
                g.claims_sent++;
                xSemaphoreGive(g_lock);
                send_packet_to(&g_group, PKT_CLAIM, flags, session, 0, NULL, 0);
                xSemaphoreTake(g_lock, portMAX_DELAY);
            }
            if (t - g.claim_start_ms >= PRE_AUDIO_DELAY_MS && g.state == ST_CLAIMING) {
                g.state = ST_TALKING;
                adpcm_state_reset(&g.enc);
                g.tx_sequence = 0;
                ESP_LOGI(TAG, "TX talking");
            }
        }
        bool can_send = g.state == ST_TALKING && t - g.tx_last_audio_ms >= 20;
        bool send_heartbeat = g.state == ST_TALKING && g.tx_directed &&
                              t - g.tx_last_heartbeat_ms >= 100;
        uint32_t heartbeat_session = g.tx_session;
        if (send_heartbeat) g.tx_last_heartbeat_ms = t;
        xSemaphoreGive(g_lock);

        if (send_heartbeat)
            send_packet_to(&g_group, PKT_HEARTBEAT, PROTO_FLAG_DIRECTED,
                           heartbeat_session, 0, NULL, 0);

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
            static uint8_t payload[ADPCM_PAYLOAD_LEN];
            adpcm_encode_frame(&g.enc, frame.pcm, payload);
            uint32_t session = g.tx_session, sequence = g.tx_sequence++;
            g.tx_last_audio_ms = t;
            xSemaphoreGive(g_lock);
            send_packet(PKT_AUDIO, session, sequence, payload, ADPCM_PAYLOAD_LEN);
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
    while (1) {
        xSemaphoreTake(g_lock, portMAX_DELAY);
        node_state_t st = g.state;
        bool local_floor = (st == ST_CLAIMING || st == ST_TALKING);

        if (st == ST_RECEIVING) {
            uint32_t age = now_ms() - g.rx_last_ms;
            if (!g.rx_ending && age > RX_TIMEOUT_MS) {   /* heartbeat lost */
                ESP_LOGI(TAG, "RX timeout, releasing floor");
                g.state = ST_IDLE;
                jb_reset();
                st = ST_IDLE;
            }
        }

        if (st == ST_RECEIVING) {
            jb_pop(mono);
            if (g.rx_ending && --g.rx_drain <= 0) {   /* tail drained */
                g.state = ST_IDLE;
                jb_reset();
            }
        } else {
            memset(mono, 0, sizeof(mono));
        }
        xSemaphoreGive(g_lock);

        /* Half duplex and the hardware mute switch both silence only I2S.
         * The node continues participating in floor control and discovery. */
        if (local_floor || button_pressed(MUTE_SWITCH_GPIO))
            memset(mono, 0, sizeof(mono));

        /* digital playback volume (SPK_VOLUME/256) with safe clipping */
#if SPK_VOLUME != 256
        for (int i = 0; i < FRAME_SAMPLES; ++i) {
            int32_t v = ((int32_t)mono[i] * SPK_VOLUME) >> 8;
            if (v > 32767)  v = 32767;
            if (v < -32768) v = -32768;
            mono[i] = (int16_t)v;
        }
#endif

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

    while (1) {
        bool matched_any = false;
        uint32_t got = 0;
        if (adc_continuous_read(adc_handle, buf, sizeof(buf), &got,
                                portMAX_DELAY) != ESP_OK)
            continue;

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
    ESP_ERROR_CHECK(i2s_channel_enable(i2s_tx));
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
        xSemaphoreTake(g_lock, portMAX_DELAY);
        node_state_t state = g.state;
        bool directed = g.tx_directed;
        bool error = (int32_t)(g.error_until_ms - t) > 0;
        xSemaphoreGive(g_lock);

        if (error) ring_controller_set(RING_ERROR);
        else if (button_pressed(MUTE_SWITCH_GPIO)) ring_controller_set(RING_MUTE);
        else if (state == ST_TALKING || state == ST_CLAIMING)
            ring_controller_set(directed ? RING_TALK_REPLY : RING_TALK_BROADCAST);
        else if (state == ST_RECEIVING) ring_controller_set(RING_SPEAKING);
        else ring_controller_set(RING_IDLE);
        ring_controller_update(t);
        vTaskDelay(pdMS_TO_TICKS(20));
    }
}

static void hello_task(void *arg)
{
    while (1) {
        /* Alias is deliberately a small, plain UTF-8 payload; apps can show
         * it immediately and query full configuration when selected. */
        const uint8_t *alias = (const uint8_t *)g_config.alias;
        uint16_t length = (uint16_t)strnlen(g_config.alias, DEVICE_ALIAS_MAX);
        send_packet_to(&g_group, PKT_HELLO, 0, 0, 0, alias, length);
        vTaskDelay(pdMS_TO_TICKS(PEER_HELLO_MS));
    }
}

/* Startup tone so the speaker/amp can be verified without a peer.
 * If you see this log but hear NOTHING, the problem is downstream of the
 * firmware: amp power, the SD/SD_MODE pin (must be tied to 3V3, not left
 * floating), DIN/BCLK/LRC wiring, or the speaker connection. */
static void startup_tone(void)
{
    static int16_t mono[FRAME_SAMPLES];
    const float freq = 1000.0f;
    static float phase = 0.0f;
    const float dphi = 2.0f * 3.14159265f * freq / SAMPLE_RATE;

    ESP_LOGI(TAG, "Playing startup tone (3 beeps @ %d Hz) on I2S "
                  "BCLK=%d LRC=%d DOUT=%d", (int)freq,
                  I2S_BCLK_GPIO, I2S_LRC_GPIO, I2S_DOUT_GPIO);

    for (int beep = 0; beep < 3; ++beep) {
        for (int f = 0; f < 20; ++f) {          /* ~400 ms of tone */
            for (int i = 0; i < FRAME_SAMPLES; ++i) {
                mono[i] = (int16_t)(16000.0f * sinf(phase));  /* ~0.5 FS, loud */
                phase += dphi;
                if (phase > 6.2831853f) phase -= 6.2831853f;
            }
            write_i2s(mono);
        }
        memset(mono, 0, sizeof(mono));
        for (int f = 0; f < 8; ++f) write_i2s(mono);   /* ~160 ms gap */
    }
    ESP_LOGI(TAG, "Startup tone done");
}

/* ======================================================================== */
/* Wi-Fi                                                                     */
/* ======================================================================== */
static void wifi_event_handler(void *arg, esp_event_base_t base,
                               int32_t id, void *data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        ESP_LOGW(TAG, "Wi-Fi disconnected, reconnecting...");
        esp_wifi_connect();
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *e = (ip_event_got_ip_t *)data;
        ESP_LOGI(TAG, "Got IP: " IPSTR, IP2STR(&e->ip_info.ip));
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
    struct ip_mreq membership = {
        .imr_multiaddr.s_addr = g_group.sin_addr.s_addr,
        .imr_interface.s_addr = htonl(INADDR_ANY),
    };
    ESP_ERROR_CHECK(setsockopt(g_sock, IPPROTO_IP, IP_ADD_MEMBERSHIP,
                               &membership, sizeof(membership)) == 0 ? ESP_OK : ESP_FAIL);
    uint8_t ttl = MULTICAST_TTL;
    ESP_ERROR_CHECK(setsockopt(g_sock, IPPROTO_IP, IP_MULTICAST_TTL,
                               &ttl, sizeof(ttl)) == 0 ? ESP_OK : ESP_FAIL);
    ESP_LOGI(TAG, "UDP ready on port %d, multicast group %s", UDP_PORT, MULTICAST_GROUP);
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
    ring_controller_init(LED_RING_GPIO, LED_RING_COUNT, g_config.led_brightness);
    startup_tone();          /* verify speaker before the network is up */
    adc_init();

    wifi_init();
    udp_init();

    xTaskCreate(net_rx_task,   "net_rx",   4096, NULL, 6, NULL);
    xTaskCreate(capture_task,  "capture",  4096, NULL, 6, NULL);
    xTaskCreate(tx_task,       "tx",       4096, NULL, 5, NULL);
    xTaskCreate(playback_task, "playback", 4096, NULL, 5, NULL);
    xTaskCreate(ring_task,     "ring",     4096, NULL, 4, NULL);
    xTaskCreate(hello_task,    "hello",    3072, NULL, 3, NULL);

    ESP_LOGI(TAG, "Intercom node %08x ready", (unsigned)NODE_ID);
}
