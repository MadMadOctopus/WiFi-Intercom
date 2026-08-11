#pragma once

#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "device_config.h"

void usb_control_start(device_config_t *config, SemaphoreHandle_t config_lock);
bool usb_control_process(device_config_t *config, const char *request,
                         char *response, size_t response_size,
                         bool *restart_required);
