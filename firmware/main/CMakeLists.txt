idf_component_register(
    SRCS
        "app_main.c"
        "adpcm.c"
        "protocol.c"
    INCLUDE_DIRS "."
    REQUIRES
        driver          # gpio + i2s_std (umbrella component, ESP-IDF 5.0-5.2)
        esp_adc         # adc_continuous
        esp_wifi
        esp_netif
        esp_event
        esp_timer
        nvs_flash
        lwip
)
