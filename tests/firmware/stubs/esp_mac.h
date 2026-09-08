#pragma once
#include <stdint.h>
#include <assert.h>
#define ESP_MAC_WIFI_STA 0
#define ESP_ERROR_CHECK(x) assert((x) == 0)
int esp_read_mac(uint8_t *, int);
