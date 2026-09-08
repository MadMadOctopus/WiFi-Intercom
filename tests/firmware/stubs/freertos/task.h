#pragma once
static inline void vTaskDelay(unsigned ticks) { (void)ticks; }
static inline void xTaskCreate(void (*task)(void *), const char *name, int stack, void *arg, int priority, void *handle)
{ (void)task; (void)name; (void)stack; (void)arg; (void)priority; (void)handle; }
