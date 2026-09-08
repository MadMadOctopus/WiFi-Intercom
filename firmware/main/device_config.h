#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define DEVICE_ALIAS_MAX 32
#define WIFI_SSID_MAX 32
#define WIFI_PASSWORD_MAX 64

#define DEVICE_FLAG_BUTTONS_SWAPPED 0x01u
#define DEVICE_FLAG_RING_180        0x02u

typedef struct {
    uint32_t device_id;
    uint32_t mesh_id;
    char alias[DEVICE_ALIAS_MAX + 1];
    char wifi_ssid[WIFI_SSID_MAX + 1];
    char wifi_password[WIFI_PASSWORD_MAX + 1];
    uint16_t speaker_volume;
    uint8_t led_brightness;
    /* Bit 0: D9 broadcasts/D10 replies. Bit 1: LED zero is rotated 180 deg.
     * This intentionally occupies the old bool byte, preserving v1 NVS data. */
    uint8_t hardware_flags;
    /* p2 soft mute is independent of the physical slider. */
    uint8_t soft_mute;
    uint8_t assistant_enabled;
    uint32_t assistant_service_id; /* stable sender ID; zero means unset */
} device_config_t;

void device_config_load(device_config_t *out);
bool device_config_save(const device_config_t *config);
bool device_config_has_wifi(const device_config_t *config);
bool device_config_apply_json(device_config_t *config, const char *json,
                              bool *restart_required);
size_t device_config_to_json(const device_config_t *config, bool hw_muted,
                             char *out, size_t out_size);
