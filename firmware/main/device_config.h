#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define DEVICE_ALIAS_MAX 32
#define WIFI_SSID_MAX 32
#define WIFI_PASSWORD_MAX 64

typedef struct {
    uint32_t device_id;
    uint32_t mesh_id;
    char alias[DEVICE_ALIAS_MAX + 1];
    char wifi_ssid[WIFI_SSID_MAX + 1];
    char wifi_password[WIFI_PASSWORD_MAX + 1];
    uint16_t speaker_volume;
    uint8_t led_brightness;
} device_config_t;

void device_config_load(device_config_t *out);
bool device_config_save(const device_config_t *config);
bool device_config_has_wifi(const device_config_t *config);
bool device_config_apply_json(device_config_t *config, const char *json,
                              bool *wifi_changed);
size_t device_config_to_json(const device_config_t *config, char *out,
                             size_t out_size);
