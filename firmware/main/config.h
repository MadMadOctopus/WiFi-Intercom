/*
 * config.h - Static configuration for the Wi-Fi PTT intercom firmware.
 *
 * ============================================================================
 *  EDIT THESE VALUES for your network and deployment.  They are hardcoded on
 *  purpose for this proof of concept.  Keep MESH_ID / UDP_PORT / audio format
 *  identical to pc_app/app.py or the two peers will not understand each other.
 * ============================================================================
 */
#ifndef INTERCOM_CONFIG_H
#define INTERCOM_CONFIG_H

/* ---- Wi-Fi credentials (2.4 GHz network required for ESP32-C3) ---------- */
#define WIFI_SSID       "YOUR_WIFI_SSID"
#define WIFI_PASSWORD   "YOUR_WIFI_PASSWORD"

/* ---- Mesh / node identity (must match the PC peer) --------------------- */
#define MESH_ID         0x4D455348u   /* "MESH" - same constant as app.py   */
#define NODE_ID         0x00000001u   /* this ESP node id (PC is 0x00000002)*/

/* ---- Peer (the PC application) ----------------------------------------- */
#define PEER_IP         "192.168.1.100"  /* IPv4 of the PC running app.py    */
#define UDP_PORT        45678           /* single UDP port for all packets  */

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

/* ---- PTT button -------------------------------------------------------- */
#define PTT_GPIO        20              /* D7, INPUT_PULLUP, pressed = LOW   */

/* ---- Floor-control / jitter timing (match app.py) ---------------------- */
#define CLAIM_COUNT         3
#define CLAIM_INTERVAL_MS   30
#define PRE_AUDIO_DELAY_MS  100
#define END_COUNT           3
#define RX_TIMEOUT_MS       750
#define JITTER_PREBUFFER    4           /* 80 ms before playout begins       */
#define REORDER_WINDOW      4

#endif /* INTERCOM_CONFIG_H */
