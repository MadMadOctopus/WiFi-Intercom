/*
 * config.h - Static configuration for the Wi-Fi PTT intercom firmware.
 *
 * ============================================================================
 *  Build-time defaults only. Production configuration is held in NVS and can
 *  be changed through USB Serial/JTAG or an in-group companion application.
 * ============================================================================
 */
#ifndef INTERCOM_CONFIG_H
#define INTERCOM_CONFIG_H

/* ---- Wi-Fi credentials (2.4 GHz network required for ESP32-C3) ---------- */
#define WIFI_SSID       "YOUR_WIFI_SSID"
#define WIFI_PASSWORD   "YOUR_WIFI_PASSWORD"

/* ---- Mesh / node identity (must match the PC peer) --------------------- */
#define DEFAULT_MESH_ID 0x4D455348u   /* "MESH"                             */
#define DEFAULT_ALIAS   "Intercom"

/* ---- Group transport (no configured peers) ---------------------------- */
#define UDP_PORT              45678
#define MULTICAST_GROUP       "239.255.42.99"
#define MULTICAST_TTL         1

/* ---- Audio format (do NOT change without changing app.py) -------------- */
#define SAMPLE_RATE     16000
#define FRAME_SAMPLES   320             /* 20 ms at 16 kHz                   */

/* ---- Microphone conditioning ------------------------------------------- */
#define MIC_GAIN        6               /* conservative capture gain         */
#define MIC_ADC_CHANNEL ADC_CHANNEL_3   /* GPIO3 / A1 = ADC1_CH3            */

/* ---- Speaker playback volume ------------------------------------------- */
/* Digital gain applied to received audio before the I2S/amp. 256 = unity;
 * 512 = 2x (+6 dB), 768 = 3x, 1024 = 4x. Too high will clip/distort loud
 * speech - for more clean headroom use the amp's GAIN pin as well (README). */
#define SPK_VOLUME      512

/* ---- I2S output pins (to MAX98357A) ------------------------------------ */
#define I2S_BCLK_GPIO   6               /* D4  -> MAX98357A BCLK             */
#define I2S_LRC_GPIO    7               /* D5  -> MAX98357A LRC              */
#define I2S_DOUT_GPIO   5               /* D3  -> MAX98357A DIN              */

/* ---- Final XIAO ESP32-C3 controls -------------------------------------- */
#define BUTTON_BROADCAST_GPIO 10        /* D10, INPUT_PULLUP, pressed = LOW  */
#define BUTTON_REPLY_GPIO      9        /* D9,  INPUT_PULLUP, pressed = LOW  */
#define MUTE_SWITCH_GPIO       8        /* D8,  INPUT_PULLUP, closed = LOW   */
#define LED_RING_GPIO         20        /* D7 -> WS2812B DI                  */
#define LED_RING_COUNT        24

/* ---- Floor-control / jitter timing (match app.py) ---------------------- */
#define CLAIM_COUNT         3
#define CLAIM_INTERVAL_MS   30
#define PRE_AUDIO_DELAY_MS  100
#define END_COUNT           3
#define RX_TIMEOUT_MS       750
#define JITTER_PREBUFFER    4           /* 80 ms before playout begins       */
#define REORDER_WINDOW      4
#define BUSY_BUFFER_MS      500
#define BUSY_BUFFER_FRAMES  (BUSY_BUFFER_MS / 20)
#define PEER_HELLO_MS       3000
#define PEER_EXPIRE_MS      10000

#endif /* INTERCOM_CONFIG_H */
