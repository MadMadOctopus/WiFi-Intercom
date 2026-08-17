# Firmware Concurrency Model

## Overview
The ESP32-S3 node in `firmware/main/app_main.c` is a set of FreeRTOS tasks
sharing one global state struct behind a single mutex. Most historical bugs in
the firmware trace to violating the locking discipline described here, so read
this before touching any task loop.

## Where it lives
- `firmware/main/app_main.c` — all task loops, the global state `g`, the
  global mutex `g_lock`, the jitter buffer (`jb_push` and friends), and packet
  dispatch (`net_rx_task`).
- `firmware/main/ota_manager.c` — OTA offer parsing, ECDSA signature
  verification and the flash/reboot flow.

## How it works
Tasks and their roles (stack sizes are set where the tasks are created near
the bottom of `app_main.c`):
- `net_rx_task` (4096-byte stack) — receives UDP packets and dispatches by
  packet type. Its stack is small: heavy work (JSON parsing of large payloads,
  mbedtls crypto) must not run on it, hand such work to `ota_task`.
- `ota_task` (7168-byte stack) — sized deliberately for mbedtls signature
  verification and the OTA download/flash sequence.
- `tx_task` — polls the PTT buttons and streams outgoing audio.
- `playback_task` — drains the jitter buffer to the speaker every 20 ms.
- `ring_task` — drives the LED ring animations.

Locking discipline:
- `g_lock` protects the state machine (`g.state`: `ST_IDLE` / `ST_CLAIMING` /
  `ST_TALKING`), the peer table, and the whole `g_config` struct
  (`device_config_apply_json` rewrites it under the lock).
- Tasks take `g_lock` with `portMAX_DELAY`, so holding it across anything slow
  (delays, network sends, crypto, `esp_restart`) freezes every other task.
  Keep critical sections to snapshot-in/snapshot-out of the fields you need.
- Readers of `g_config` fields in task loops must snapshot them under `g_lock`
  once per iteration; unlocked reads race with configuration updates.
- Any check-then-act across a lock release (e.g. "state was idle, so start
  OTA") is a TOCTOU bug: re-validate state in the same critical section that
  commits the action.

## Constraints and gotchas
- RTP sequence numbers are 16-bit and wrap about every 22 minutes of
  continuous audio; all sequence comparisons (jitter buffer expected-sequence
  logic on both firmware and companion) must use wrap-aware signed 16-bit
  deltas, never monotonic counters.
- Payloads handed from `net_rx_task` to another task must be copied, not
  pointed into the receive buffer.
- Every `xSemaphoreTake(g_lock, ...)` needs exactly one matching
  `xSemaphoreGive` on every code path, including restart/error paths.

## Related
- [System overview](./architecture-system-overview.md)
- [Network trust model](./security-network-trust-model.md)
