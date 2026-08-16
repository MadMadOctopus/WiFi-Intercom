# Follow-up tasks — after the 0.8 companion baseline

Captured 2026-08-16, at the point the companion redesign (p2) landed on `main` as
version 0.8.0. These are deferred items, in rough priority order. Nothing here
blocks the 0.8 baseline; they are the next round of work.

## 1. Silent audio-transmit failure ("we lost the Bench Intercom")

**Symptom seen:** after relabeling a device, it announced (HELLO), reserved the
floor (CLAIM) and showed `SPEAKING`, but sent **zero RTP audio** — no device or
the companion heard it, though it could still receive and play. A reboot fixed
it. Diagnostics showed repeated `rx finish sender=… udp=0 decoded=0`.

**Root cause:** control traffic (CLAIM/HEARTBEAT/END) and audio go out on two
different sockets — `g_sock` (45678) and `g_rtp_sock` (45679) in
`firmware/main/app_main.c`. `send_rtp_to()` silently returns when
`g_rtp_sock < 0`, so the audio path can die while control/discovery stay healthy
and nothing surfaces it. The relabel (a config write, over a flaky USB link) left
the RTP socket wedged until reboot.

- **1a. Firmware — self-heal + stop failing silently.** In `send_rtp_to()`, if
  `g_rtp_sock < 0` (or `sendto` errors persistently), log a warning and re-create
  the RTP socket instead of dropping the frame. Consider the same for `g_sock`.
- **1b. Firmware — root-cause trace.** Find what a config/alias write (network
  `CONFIG_SET` and the USB path) does that can invalidate `g_rtp_sock`. Alias is
  meant to apply without restart (spec §2.2); confirm that path never disturbs
  the sockets.
- **1c. Companion — make it diagnosable.** When a receive session finishes with
  `udp == 0` (device claimed the floor but sent no audio), surface it: an Activity
  line / diagnostics note such as *"<alias> claimed the floor but sent no audio —
  the device may need a reboot."* Additive only; does not change how the companion
  talks to devices. Touch points: `Core/ReceiveSession.cs` (finish stats),
  `MainForm.cs` (`OnIntercomState` / activity).

## 2. USB communication stack review (companion + firmware)

The USB provisioning/relabel link is flaky on both ends — reads errored roughly
half the time during relabeling, and item 1 was triggered through it.

- **Companion** — `Core/UsbConfigurationClient.cs`: review framing, timeouts and
  add retries/settling so identify + write are reliable; the USB page in
  `Views/Settings/UsbPage.cs` should reflect failures clearly (it already colours
  the identify status). Consider rebooting the device after a USB alias write so a
  half-applied config cannot linger (item 1).
- **Firmware** — `firmware/main/usb_control.c`: audit the line-JSON parser, buffer
  handling and error responses; make partial/garbled reads fail cleanly rather
  than half-applying.

## 3. OTA update testing (needs multiple firmware builds)

The OTA queue UI and gating are in place but have not been exercised end-to-end
against real transitions.

- **3a. Single device** — offer a newer signed package to one device, confirm the
  write, reboot, and re-announce of the offered version (spec 4.7/32), plus the
  rollback path when a device does not report healthy (4.7/34).
- **3b. Scheduled / queued multi-device** — `Start queue…` runs devices strictly
  one at a time, each confirming before the next starts (4.7/32), with local PTT
  and Silence disabled for the duration (4.7/33).
- **Prereq:** build several signed firmware versions (e.g. 0.7.9 → 0.8.0 → 0.8.1)
  so real version transitions, "not eligible" states and rollback can be tested.

---

Design references: `docs/design/companion-redesign-spec.md` (§2 protocol, §4
acceptance criteria), `docs/design/companion-redesign-handover.md`.
