#pragma once
#include <stddef.h>
#include <assert.h>
typedef struct { unsigned tx_buffer_size; } usb_serial_jtag_driver_config_t;
#define USB_SERIAL_JTAG_DRIVER_CONFIG_DEFAULT() (usb_serial_jtag_driver_config_t){256}
static inline int usb_serial_jtag_driver_install(const usb_serial_jtag_driver_config_t *cfg) { assert(cfg->tx_buffer_size >= 768); return 0; }
static inline int usb_serial_jtag_write_bytes(const void *p, size_t n, unsigned t) { (void)p; (void)t; return (int)n; }
static inline int usb_serial_jtag_read_bytes(void *p, size_t n, unsigned t) { (void)p; (void)n; (void)t; return 0; }
static inline int usb_serial_jtag_wait_tx_done(unsigned t) { (void)t; return 0; }
