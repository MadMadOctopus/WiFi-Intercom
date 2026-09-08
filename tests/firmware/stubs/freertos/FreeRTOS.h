#pragma once
#include <assert.h>
typedef unsigned TickType_t;
#define portMAX_DELAY 0xffffffffu
#define pdMS_TO_TICKS(ms) (ms)
#define configASSERT(x) assert(x)
