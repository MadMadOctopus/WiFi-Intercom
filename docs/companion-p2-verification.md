# Companion p2 verification record

Date: 2026-08-14

## Completed in this workspace

- Static contract check: p2 is declared in both protocol implementations;
  desktop IH3 parser/builder, firmware IH3 builder, NVS v1-to-v2 migration,
  mesh validation, mute gate, card UI, remembered-device store, OTA
  acknowledgement and tray implementation are all present.
- `git diff --check`: passed (no whitespace errors).
- Source cross-reference: all `Peer` and `DeviceConfiguration` call sites were
  updated for the additional p2 fields.
- ESP-IDF 5.4.4 firmware build: passed. `wifi_intercom.bin` is `0xff760`
  bytes, leaving `0x708a0` bytes (31%) free in the smallest OTA app partition.
- Windows .NET 10 companion build: passed with zero warnings and zero errors.
- Flash and USB verification: passed on COM6 (MAC `94:a9:90:67:7b:2c`) and
  COM9 (MAC `94:a9:90:68:8e:dc`). Normal flashing preserved each device's NVS
  configuration. Both p2 USB replies included string `mesh_id`, `soft_mute`
  and `hw_muted`; a reversible `soft_mute` true/false round-trip passed and
  both devices were restored to false.

## Not runnable in this environment

The Windows Forms companion compiled successfully through the installed Windows
toolchain. A visual pass and live device-card/PTT test still require launching
the GUI with the intended Windows audio devices.

## Hardware/Windows acceptance pass to run next

1. Flash a p1-configured XIAO with p2 and confirm its existing configuration
   survives, IH3 appears in packet capture, and the p2 companion still shows a
   p1 node as Legacy.
3. Toggle physical/soft mute and verify the ≤4 s discovery update and silent
   playout combinations in 4.1–4.4 of the redesign brief.
4. At 1024×700 and 1280×820, announce 16 devices and inspect the three-column
   card grid, filter stability, speaking pulse and offline promotion.
5. Execute directed/broadcast/reply PTT, group migration and an OTA queue with
   at least two devices, including a forced failure/rollback.

Global hold hotkeys are intentionally pending a low-level keyboard-hook
implementation: the standard Windows hotkey API cannot report release events,
which would violate the PTT release requirement.
