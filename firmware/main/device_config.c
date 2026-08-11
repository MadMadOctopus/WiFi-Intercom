#include "device_config.h"

#include <string.h>
#include <stdlib.h>

#include "esp_log.h"
#include "esp_mac.h"
#include "nvs.h"
#include "nvs_flash.h"
#include "cJSON.h"

#include "config.h"

static const char *TAG = "device_config";
static const char *NVS_NAMESPACE = "intercom";
static const char *NVS_KEY = "config_v1";

typedef struct {
    uint32_t version;
    device_config_t value;
} stored_config_t;

static uint32_t default_device_id(void)
{
    uint8_t mac[6] = {0};
    ESP_ERROR_CHECK(esp_read_mac(mac, ESP_MAC_WIFI_STA));
    return ((uint32_t)mac[2] << 24) | ((uint32_t)mac[3] << 16) |
           ((uint32_t)mac[4] << 8) | mac[5];
}

static void make_defaults(device_config_t *out)
{
    memset(out, 0, sizeof(*out));
    out->device_id = default_device_id();
    out->mesh_id = DEFAULT_MESH_ID;
    strncpy(out->alias, DEFAULT_ALIAS, DEVICE_ALIAS_MAX);
    strncpy(out->wifi_ssid, WIFI_SSID, WIFI_SSID_MAX);
    strncpy(out->wifi_password, WIFI_PASSWORD, WIFI_PASSWORD_MAX);
    out->speaker_volume = SPK_VOLUME;
    /* A restrained default cuts WS2812 current transients that can otherwise
     * bleed into the nearby class-D amplifier on USB-powered assemblies. */
    out->led_brightness = 48;
    out->hardware_flags = 0;
}

void device_config_load(device_config_t *out)
{
    make_defaults(out);
    nvs_handle_t handle;
    if (nvs_open(NVS_NAMESPACE, NVS_READONLY, &handle) != ESP_OK) return;

    stored_config_t stored = {0};
    size_t len = sizeof(stored);
    esp_err_t err = nvs_get_blob(handle, NVS_KEY, &stored, &len);
    nvs_close(handle);
    if (err == ESP_OK && len == sizeof(stored) && stored.version == 1 &&
        stored.value.device_id != 0) {
        *out = stored.value;
    } else if (err != ESP_ERR_NVS_NOT_FOUND) {
        ESP_LOGW(TAG, "Ignoring invalid saved configuration: %s", esp_err_to_name(err));
    }
}

bool device_config_save(const device_config_t *config)
{
    stored_config_t stored = {.version = 1, .value = *config};
    nvs_handle_t handle;
    if (nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle) != ESP_OK) return false;
    esp_err_t err = nvs_set_blob(handle, NVS_KEY, &stored, sizeof(stored));
    if (err == ESP_OK) err = nvs_commit(handle);
    nvs_close(handle);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "Could not save configuration: %s", esp_err_to_name(err));
        return false;
    }
    return true;
}

bool device_config_has_wifi(const device_config_t *config)
{
    return config->wifi_ssid[0] != '\0' &&
           strcmp(config->wifi_ssid, "YOUR_WIFI_SSID") != 0;
}

static bool assign_string(cJSON *root, const char *key, char *out, size_t size)
{
    cJSON *item = cJSON_GetObjectItemCaseSensitive(root, key);
    if (!cJSON_IsString(item) || !item->valuestring) return false;
    strncpy(out, item->valuestring, size - 1);
    out[size - 1] = '\0';
    return true;
}

bool device_config_apply_json(device_config_t *config, const char *json,
                              bool *restart_required)
{
    cJSON *root = cJSON_Parse(json);
    if (!root) return false;
    device_config_t updated = *config;
    char old_ssid[WIFI_SSID_MAX + 1];
    char old_password[WIFI_PASSWORD_MAX + 1];
    uint8_t old_hardware_flags = updated.hardware_flags;
    uint8_t old_brightness = updated.led_brightness;
    memcpy(old_ssid, updated.wifi_ssid, sizeof(old_ssid));
    memcpy(old_password, updated.wifi_password, sizeof(old_password));
    assign_string(root, "alias", updated.alias, sizeof(updated.alias));
    assign_string(root, "ssid", updated.wifi_ssid, sizeof(updated.wifi_ssid));
    assign_string(root, "password", updated.wifi_password, sizeof(updated.wifi_password));
    cJSON *mesh = cJSON_GetObjectItemCaseSensitive(root, "mesh_id");
    cJSON *volume = cJSON_GetObjectItemCaseSensitive(root, "speaker_volume");
    cJSON *brightness = cJSON_GetObjectItemCaseSensitive(root, "led_brightness");
    cJSON *buttons_swapped = cJSON_GetObjectItemCaseSensitive(root, "buttons_swapped");
    cJSON *ring_orientation = cJSON_GetObjectItemCaseSensitive(root, "ring_orientation");
    if (cJSON_IsNumber(mesh) && mesh->valuedouble >= 1 && mesh->valuedouble <= UINT32_MAX)
        updated.mesh_id = (uint32_t)mesh->valuedouble;
    if (cJSON_IsNumber(volume) && volume->valuedouble >= 64 && volume->valuedouble <= 1024)
        updated.speaker_volume = (uint16_t)volume->valuedouble;
    if (cJSON_IsNumber(brightness) && brightness->valuedouble >= 0 && brightness->valuedouble <= 255)
        updated.led_brightness = (uint8_t)brightness->valuedouble;
    if (cJSON_IsBool(buttons_swapped))
        updated.hardware_flags = cJSON_IsTrue(buttons_swapped)
            ? (updated.hardware_flags | DEVICE_FLAG_BUTTONS_SWAPPED)
            : (updated.hardware_flags & ~DEVICE_FLAG_BUTTONS_SWAPPED);
    if (cJSON_IsNumber(ring_orientation) &&
        (ring_orientation->valuedouble == 0 || ring_orientation->valuedouble == 180)) {
        updated.hardware_flags = ring_orientation->valuedouble == 180
            ? (updated.hardware_flags | DEVICE_FLAG_RING_180)
            : (updated.hardware_flags & ~DEVICE_FLAG_RING_180);
    }
    cJSON_Delete(root);
    if (!device_config_save(&updated)) return false;
    if (restart_required)
        *restart_required = strcmp(old_ssid, updated.wifi_ssid) != 0 ||
                            strcmp(old_password, updated.wifi_password) != 0 ||
                            old_hardware_flags != updated.hardware_flags ||
                            old_brightness != updated.led_brightness;
    *config = updated;
    return true;
}

size_t device_config_to_json(const device_config_t *config, char *out,
                             size_t out_size)
{
    cJSON *root = cJSON_CreateObject();
    cJSON_AddStringToObject(root, "type", "config");
    cJSON_AddNumberToObject(root, "device_id", config->device_id);
    cJSON_AddNumberToObject(root, "mesh_id", config->mesh_id);
    cJSON_AddStringToObject(root, "alias", config->alias);
    cJSON_AddStringToObject(root, "ssid", config->wifi_ssid);
    cJSON_AddNumberToObject(root, "speaker_volume", config->speaker_volume);
    cJSON_AddNumberToObject(root, "led_brightness", config->led_brightness);
    cJSON_AddBoolToObject(root, "buttons_swapped",
                          (config->hardware_flags & DEVICE_FLAG_BUTTONS_SWAPPED) != 0);
    cJSON_AddNumberToObject(root, "ring_orientation",
                            (config->hardware_flags & DEVICE_FLAG_RING_180) ? 180 : 0);
    char *encoded = cJSON_PrintUnformatted(root);
    cJSON_Delete(root);
    if (!encoded) return 0;
    size_t length = strlen(encoded);
    if (length >= out_size) length = 0;
    if (length) memcpy(out, encoded, length + 1);
    free(encoded);
    return length;
}
