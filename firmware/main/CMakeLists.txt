idf_component_register(
    SRCS
        "app_main.c"
        "device_config.c"
        "protocol.c"
        "ring_controller.cpp"
        "usb_control.c"
    INCLUDE_DIRS "."
    REQUIRES
        driver          # gpio + i2s_std (umbrella component, ESP-IDF 5.0-5.2)
        esp_driver_rmt
        esp_driver_usb_serial_jtag
        json
        esp_adc         # adc_continuous
        esp_wifi
        esp_netif
        esp_event
        esp_timer
        nvs_flash
        lwip
        espressif__esp_audio_codec
)

# Enabled only for the hardware diagnostic image built with
# -DINTERCOM_USB_RAW_MIC_CAPTURE=ON. The production image has no raw mic
# stream and retains its normal USB configuration channel.
if(INTERCOM_USB_RAW_MIC_CAPTURE)
    target_compile_definitions(${COMPONENT_LIB} PRIVATE INTERCOM_USB_RAW_MIC_CAPTURE=1)
endif()
