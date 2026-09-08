#include "usb_control.h"

#include <string.h>
#include <stdio.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "driver/usb_serial_jtag.h"
#include "cJSON.h"
#include "esp_system.h"
#include "driver/gpio.h"
#include "config.h"

#ifndef INTERCOM_USB_RAW_MIC_CAPTURE
#define INTERCOM_USB_RAW_MIC_CAPTURE 0
#endif

static device_config_t *s_config;
static SemaphoreHandle_t s_lock;
static SemaphoreHandle_t s_write_lock;

bool usb_control_process(device_config_t *config, const char *request,
                         char *response, size_t response_size,
                         bool *restart_required)
{
    if (restart_required) *restart_required = false;
    cJSON *root = cJSON_Parse(request);
    cJSON *command = root ? cJSON_GetObjectItemCaseSensitive(root, "cmd") : NULL;
    bool get = cJSON_IsString(command) && strcmp(command->valuestring, "get") == 0;
    cJSON_Delete(root);
    bool hw_muted = gpio_get_level(MUTE_SWITCH_GPIO) == 0;
    if (get) return device_config_to_json(config, hw_muted, response, response_size) != 0;
    if (!device_config_apply_json(config, request, restart_required)) {
        snprintf(response, response_size, "{\"type\":\"error\",\"message\":\"invalid configuration\"}");
        return false;
    }
    return device_config_to_json(config, hw_muted, response, response_size) != 0;
}

bool usb_control_request(const char *request, char *response, size_t response_size,
                         bool *restart_required)
{
    xSemaphoreTake(s_write_lock, portMAX_DELAY);
    device_config_t working;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    working = *s_config;
    xSemaphoreGive(s_lock);
    bool ok = usb_control_process(&working, request, response, response_size, restart_required);
    if (ok) {
        xSemaphoreTake(s_lock, portMAX_DELAY);
        *s_config = working;
        xSemaphoreGive(s_lock);
    }
    xSemaphoreGive(s_write_lock);
    return ok;
}

static void usb_task(void *arg)
{
    char line[768] = {0};
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
            char response[768] = {0};
            bool restart_required = false;
            usb_control_request(line, response, sizeof(response), &restart_required);
            strncat(response, "\n", sizeof(response) - strlen(response) - 1);
            usb_serial_jtag_write_bytes(response, strlen(response), pdMS_TO_TICKS(100));
            used = 0;
            if (restart_required) {
                /* Wi-Fi, LED brightness and orientation are initialised at
                 * boot. Ensure the companion receives its acknowledgement first. */
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
    s_write_lock = xSemaphoreCreateMutex();
    configASSERT(s_write_lock);
    usb_serial_jtag_driver_config_t cfg = USB_SERIAL_JTAG_DRIVER_CONFIG_DEFAULT();
    /* The driver enqueues each write atomically; a full config reply exceeds
     * its default 256-byte TX ring, so provision room for the entire line. */
    cfg.tx_buffer_size = 1024;
#if INTERCOM_USB_RAW_MIC_CAPTURE
    /* Raw capture frames are 652 bytes. The driver's default 256-byte ring
     * rejects each frame atomically, so use enough room for several complete
     * diagnostic packets. This is compiled out of the production image. */
    cfg.tx_buffer_size = 2048;
#endif
    ESP_ERROR_CHECK(usb_serial_jtag_driver_install(&cfg));
    xTaskCreate(usb_task, "usb_control", 6144, NULL, 4, NULL);
}
