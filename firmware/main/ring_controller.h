#pragma once

#include <stdint.h>

typedef enum {
    RING_IDLE,
    RING_TALK_BROADCAST,
    RING_TALK_REPLY,
    RING_SPEAKING,
    RING_MUTE,
    RING_ERROR,
} ring_mode_t;

void ring_controller_init(uint8_t gpio, uint8_t count, uint8_t brightness);
void ring_controller_set(ring_mode_t mode);
void ring_controller_update(uint32_t now_ms);

