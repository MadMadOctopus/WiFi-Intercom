#pragma once
static inline void esp_restart(void) { assert(!"hardware restart must not be called in a host test"); }
