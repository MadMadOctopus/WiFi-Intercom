#include "usb_control.h"

#include <string.h>
#include <stdio.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "driver/usb_serial_jtag.h"
#include "cJSON.h"
#include "esp_system.h"

static device_config_t *s_config;
static SemaphoreHandle_t s_lock;

bool usb_control_process(device_config_t *config, const char *request,
                         char *response, size_t response_size,
                         bool *wifi_changed)
{
    if (wifi_changed) *wifi_changed = false;
    cJSON *root = cJSON_Parse(request);
    cJSON *command = root ? cJSON_GetObjectItemCaseSensitive(root, "cmd") : NULL;
    bool get = cJSON_IsString(command) && strcmp(command->valuestring, "get") == 0;
    cJSON_Delete(root);
    if (get) return device_config_to_json(config, response, response_size) != 0;
    if (!device_config_apply_json(config, request, wifi_changed)) {
        snprintf(response, response_size, "{\"type\":\"error\",\"message\":\"invalid configuration\"}");
        return false;
    }
    return device_config_to_json(config, response, response_size) != 0;
}

static void usb_task(void *arg)
{
    char line[256] = {0};
    size_t used = 0;
    const char hello[] = "{\"type\":\"intercom-usb\",\"version\":1}\n";
    usb_serial_jtag_write_bytes(hello, sizeof(hello) - 1, pdMS_TO_TICKS(20));
    while (1) {
        char incoming[32];
        int n = usb_serial_jtag_read_bytes(incoming, sizeof(incoming), pdMS_TO_TICKS(100));
        for (int i = 0; i < n; ++i) {
            if (incoming[i] == '\r') continue;
            if (incoming[i] != '\n' && used + 1 < sizeof(line)) {
                line[used++] = incoming[i];
                continue;
            }
            if (used == 0) continue;
            line[used] = '\0';
            char response[256] = {0};
            bool wifi_changed = false;
            xSemaphoreTake(s_lock, portMAX_DELAY);
            usb_control_process(s_config, line, response, sizeof(response), &wifi_changed);
            xSemaphoreGive(s_lock);
            strncat(response, "\n", sizeof(response) - strlen(response) - 1);
            usb_serial_jtag_write_bytes(response, strlen(response), pdMS_TO_TICKS(100));
            used = 0;
            if (wifi_changed) {
                /* A changed SSID/password is not live in esp_wifi. Ensure the
                 * companion receives its acknowledgement before rebooting. */
                usb_serial_jtag_wait_tx_done(pdMS_TO_TICKS(250));
                vTaskDelay(pdMS_TO_TICKS(250));
                esp_restart();
            }
        }
    }
}

void usb_control_start(device_config_t *config, SemaphoreHandle_t config_lock)
{
    s_config = config;
    s_lock = config_lock;
    usb_serial_jtag_driver_config_t cfg = USB_SERIAL_JTAG_DRIVER_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(usb_serial_jtag_driver_install(&cfg));
    xTaskCreate(usb_task, "usb_control", 4096, NULL, 4, NULL);
}
