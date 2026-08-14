# Companion redesign and protocol p2 — implementation plan

This plan implements the companion redesign brief in
`design-materials/docs/companion-redesign-spec.md` while retaining the working
audio codec, RTP transport, peer-snapshot floor control and non-blocking UI
contract documented in `windows-companion-design.md`.

## Scope and compatibility decisions

- Keep the `PTT1` 32-byte big-endian control header and RTP/IMA-ADPCM media
  byte-for-byte unchanged.
- Make the companion and firmware protocol revision **p2**.  p2 nodes parse
  and interoperate with p1 HELLO/audio/discovery; p1 devices remain visible as
  Legacy cards with p2-only controls unavailable.
- Keep the existing trusted-LAN model.  Group IDs are four-character
  `A–Z0–9` values carried in the existing numeric header field; no encryption
  or authentication is introduced.

## Delivery sequence

1. **Shared p2 model and test seam.** Add `IH3` parsing/building, HELLO flags,
   typed configuration fields and group-ID conversion/validation. Add focused
   protocol tests for IH1/IH2/IH3 and p1/p2 packet acceptance.
2. **Firmware p2.** Migrate the persisted NVS record without losing p1
   configuration, add `soft_mute`, emit slider/talking state in IH3, validate
   mesh/device changes, and use both mute sources as the playout gate. Ensure
   network and USB replies include all non-secret fields.
3. **Companion state and persistence.** Make the active group configurable,
   retain p2 peer state, and add `%AppData%/WiFi-Intercom/devices.json` for
   locally remembered offline devices. Discovery remains the liveness source.
4. **Talk surface.** Replace the list/grid and shared editor with a resizable
   three-column device-card surface, filter/count chips, speaking pulse,
   per-card directed PTT, debounced volume, soft-mute and overflow actions.
5. **Settings and dialogs.** Move USB provisioning, identity/audio, group/ID
   changes, diagnostics and OTA queue into Settings; build safe Configure,
   removal and acknowledgement-gated OTA dialogs.
6. **Lifecycle and verification.** Add tray/hotkey behavior where platform
   support is available, compile the companion and firmware, execute unit and
   structural acceptance checks, and document those requiring real hardware.

## Acceptance mapping

- Protocol/firmware: 4.1 (1–9); desktop protocol tests cover parsers and field
  rules, while XIAO hardware checks cover slider, playout and migration.
- Cards/offline/filtering/pulse: 4.2 and 4.4–4.5; automated model tests plus
  a desktop visual/manual pass at 1024 and 1280 pixels.
- PTT: 4.3; preserve the existing `ReceiveSession` state-machine behavior and
  verify it against peer snapshots; actual directed audio needs two endpoints.
- Group IDs and OTA: 4.6–4.7; validate before network writes and exercise queue
  sequencing with fakes where practical; device reboot/rollback needs hardware.
- USB/tray/diagnostics: 4.8; test parsing and non-audio startup locally; USB,
  global hotkeys, tray icon and live audio require Windows/device execution.

## Constraints

- UI controls are only updated on the UI thread. Network/audio/configuration
  work remains asynchronous; an audio failure must not stop discovery.
- Never persist Wi-Fi passwords or store an IP-address peer list.
- Apply remote group changes sequentially, continue after individual failure,
  and prevent a visible device-ID collision before any packet is sent.
