# Follow-up tasks — after the 0.8 companion baseline

Captured 2026-08-16, at the point the companion redesign (p2) landed on `main` as
version 0.8.0. These are deferred items, in rough priority order. Nothing here
blocks the 0.8 baseline; they are the next round of work.

Since then, multi-group support (`adjustments/02-multi-group.md`) landed on
`main` as **0.9.0**. The **companion side is complete** — it joins several
groups, hears all of them, partitions the grid by group, broadcasts to all
groups by default or one on request, and moves devices between groups. The one
outstanding dependency it cannot satisfy alone is **cross-group discovery**
(item 4): seeing a group this companion has never joined. That needs a small
firmware change and a reflash.

**PR #4** (external, 2026-08-18) then landed on top of 0.9, fixing the entire
security review below (§5) and its cleanups (§6). Its companion changes were
built clean and merged with the multi-group code; its firmware changes are
reviewed but **not yet compiled or flashed here** — see the §5 status note.

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

## 4. Firmware — cross-group discovery (enables "Other groups on this network")

The multi-group companion shows an *Other groups on this network* panel for
groups it can see but has not joined. Populating it needs the companion to
discover devices in a group it is **not** a member of — and with today's
protocol that is impossible on a typical LAN.

### Why it can't work today

Discovery has two possible channels, and for a group the companion hasn't joined
both are closed on a normal consumer AP:

1. **The device's proactive multicast HELLO** (`mesh_id` = its group, sent to
   239.255.42.99). The companion already accepts any `mesh_id`, so it would learn
   the group if this arrived — but consumer APs commonly **block multicast
   between Wi-Fi clients** (IGMP snooping / client isolation). That is the very
   reason the firmware has a unicast `reply_hello` fallback.

2. **The companion's unicast HELLO probe → device replies.** This is the reliable
   channel. But `firmware/main/app_main.c` `net_rx_task` drops the probe before it
   can answer:

   ```c
   if (!protocol_parse(buf, n, MESH_ID, NODE_ID, &pkt)) continue;  // rejects a foreign mesh_id
   ...
   case PKT_HELLO: reply_hello(&src); break;                        // never reached for a foreign HELLO
   ```

   The companion cannot know a foreign group's 4-character code (the code space is
   36⁴ ≈ 1.7M, so it can't be brute-forced), so it cannot craft a probe the
   foreign device will accept.

Net: a group the companion has **never heard** is undiscoverable on an isolating
AP. The companion-side mitigation already shipped (`IntercomNode.seenGroupCodes`
— keep probing every group ever heard, refresh all peers on their own group)
only rescues groups heard **at least once** (joined-then-left, or an AP that does
forward device multicast). It cannot bootstrap a brand-new foreign group.

### The fix — make HELLO a group-agnostic discovery primitive

HELLO is inherently cross-group ("I exist, here is my group"); only floor, audio
and config are group-scoped. In `net_rx_task`, parse without the mesh filter and
gate only the non-HELLO packet types by group:

```c
if (!protocol_parse_any(buf, n, NODE_ID, &pkt)) continue;   // parse; keep pkt.mesh_id, don't filter
bool same_group = (pkt.mesh_id == MESH_ID);

if (pkt.type == PKT_HELLO) {
    reply_hello(&src);                     // answer discovery from ANY group…
    if (same_group) peer_seen(&pkt, &src); // …but only same-group peers enter our audio fan-out
    continue;
}
if (!same_group) continue;                 // CLAIM / HEARTBEAT / END / CONFIG stay strictly group-scoped
peer_seen(&pkt, &src);
switch (pkt.type) { /* CLAIM / END / HEARTBEAT / CONFIG … as today */ }
```

Two points are load-bearing:

- The device answers a HELLO from **any** group, and its reply carries its **real**
  `mesh_id`, so the asker learns both "a device is here" and "it is in group X".
- It must **not** `peer_seen` a foreign device. `send_to_active_peers` /
  `send_rtp_to_active_peers` iterate `g.peers` with no group filter, so a foreign
  peer in that table would leak this device's audio into another group. Keeping
  `peer_seen` same-group-only preserves today's audio/floor behaviour exactly.

This needs a `protocol_parse` variant that returns `mesh_id` instead of filtering
on it (mirrors what the companion's `Protocol.TryParse` already does).

### Consequences and scope

- **Companion:** no further change required. It already probes on unicast, accepts
  any `mesh_id`, and tags peers by group. Once devices answer cross-group HELLOs,
  a single probe (on any group the companion is in) draws replies from every
  device on the subnet, and *Other groups on this network* populates.
- **Firmware:** the change above, then a **reflash of every device**. This is the
  only reason it is deferred.
- **Security:** within the trusted-LAN threat model (spec §5: group is
  trusted-LAN only), "any node can enumerate the groups on the LAN" is acceptable.
- **Alternative considered:** a dedicated `PKT_DISCOVER` request/response instead
  of overloading HELLO — cleaner separation but more protocol surface. Not worth
  it here: the HELLO change is ~10 lines and reuses the reliable reply path the
  system already depends on.

## 5. Security & robustness — external code review (2026-08-17) — ✅ CLOSED by PR #4

A read-only review of the whole tree (firmware + companion, ~9.5k LOC) flagged
eight bugs in the handling of **untrusted network input**: 5.1–5.3 are remotely
triggerable denial-of-service, 5.4–5.6 can crash or reboot a node.

**Status:** all eight are fixed by **PR #4** (`fix(companion): correct BUSY
session, bound jitter buffer, contain handler faults` + `fix(firmware): claim
OTA slot only after signature verification, move blocking work off g_lock`),
merged into `main` as part of the 0.9 line on 2026-08-18. The companion side was
built clean here (0 warnings, 0 errors) and the fixes were combined with the
0.9 multi-group code during the merge. **The firmware side was reviewed but not
compiled in this environment (no ESP-IDF toolchain); build + flash + on-device
verification is still owed** — fold it into items 2/3 below. The original
findings are kept here for the record.

- **✅ 5.1. Companion — one malformed UDP packet permanently kills the receive
  loop (one-packet DoS).** `Core/IntercomNode.cs` `ParseConfigurationReply` /
  `ParseOtaStatus` catch only `JsonException`. `JsonDocument.Parse` accepts any
  syntactically valid JSON, but `GetInt32()`/`GetBoolean()`/`GetString()` throw
  `InvalidOperationException`/`FormatException`/`OverflowException` on a
  wrong-typed value — e.g. a peer sends `ConfigReply {"speaker_volume":"x"}` or
  `OtaStatus {"progress":"n/a"}` (`Protocol.TryParse` only checks magic + mesh
  id). These run synchronously from `ReceiveLoopAsync`, whose `catch` only
  covers `OperationCanceled`/`ObjectDisposed`/`SocketException`, so the
  exception escapes the fire-and-forget task and the loop dies: discovery, PTT
  claim and config replies stop until the app is restarted. **Fix:** also catch
  `InvalidOperationException`/`FormatException`/`OverflowException` in both
  parsers, **and** add a defensive per-datagram `try/catch` in `ReceiveLoopAsync`
  that logs and skips any unexpected packet-handling failure so the loop lives.

- **✅ 5.2. Both sides — jitter buffer ignores 16-bit sequence wraparound.**
  `Audio/JitterBuffer.cs` `expectedSequence` is a monotonic `uint`;
  `firmware/main/app_main.c` uses a `uint32_t` — but the RTP sequence is 16-bit
  and legitimately wraps. A continuous PTT session past 65536 frames × 20 ms
  (~21.8 min) rolls the `ushort` 65535→0 while `expectedSequence` is already
  >65536 and never resets, so `sequence < expectedSequence` is permanently true
  and **all further audio is dropped** for the rest of the session. **Fix:**
  companion → `ushort` + signed 16-bit delta (`Distance`); firmware → add
  `jb_seq_delta()` and rewrite the `jb_push`/`jb_pop` comparisons wrap-aware.
  Keep call-site types aligned (`RtpPacket.Sequence` = `ushort`, `handle_rtp` =
  `uint16_t`).

- **✅ 5.3. Both sides — a forged Busy packet cancels any channel claim.** Unlike
  the audio/claim path (which checks `SenderId`/`SessionId`), the Busy branch in
  `Core/ReceiveSession.cs` and `firmware/main/app_main.c` checks only
  `state == Claiming`. Any node (the 4-char mesh id is learned from an
  unauthenticated HELLO) can spray forged Busy packets and knock every claimer
  straight back to Idle — network-wide PTT denial from one spoofed packet.
  **Fix (note the deviation):** the session id inside a Busy carries the
  *responder's* session, not our claim's, so an exact match is impossible without
  a wire-format change. Instead: the **companion** accepts a Busy only from the
  node the claim was addressed to and from its known address; the **firmware**
  accepts one only while `ST_CLAIMING`, with a claim already sent, from a foreign
  sender id, and only if it wins by the same arbitration rule CLAIM uses. The two
  rules differ but both are strictly stronger than today's "any Busy kills the
  claim."

- **✅ 5.4. Firmware — `handle_config_packet` holds `g_lock` across
  `vTaskDelay(300)` + `esp_restart`.** In `app_main.c` `g_lock` is taken and only
  released after the restart path, so a restart-requiring `CONFIG_SET` freezes
  `tx_task`/`playback_task`/`ring_task` for 300 ms: an active call stalls, the
  END packet never leaves, and remote peers wait out `RX_TIMEOUT_MS`. **Fix:**
  `handle_config_packet` returns a flag; the delay and `esp_restart` run in
  `net_rx_task` *after* `xSemaphoreGive`.

- **✅ 5.5. Firmware — TOCTOU between "channel idle" and OTA capture.** In
  `app_main.c`, `idle = (g.state == ST_IDLE)` is read under `g_lock`, then the
  lock is released before `ota_manager_offer()` sets `s_active`. A PTT edge in
  that window flips the state to `ST_CLAIMING`/`ST_TALKING`, but the stale
  `idle=true` still accepts the offer — so OTA can `esp_restart()` mid-call.
  **Fix:** make `ota_manager_offer` non-blocking so the idle check and `s_active`
  capture happen under one `g_lock`; additionally have both PTT-start paths in
  `tx_task` check `!ota_manager_is_active()` under the lock, for full mutual
  exclusion.

- **✅ 5.6. Firmware — OTA signature verification runs on the `net_rx_task` stack
  (4096 B), not `ota_task` (7168 B).** On a `PKT_OTA_OFFER` while idle,
  `ota_manager.c` has `net_rx_task` call `ota_manager_offer → parse_offer →
  verify_offer_signature`, which stacks `mbedtls_base64_decode`,
  `mbedtls_pk_parse_public_key`, `mbedtls_pk_verify` and buffers on top of the
  JSON-parse frame — any peer sending a plausible offer can overflow the
  `net_rx_task` stack and crash/reboot the node regardless of signature validity.
  **Fix:** `net_rx_task` only copies the offer (≤320 B) into a staging slot and
  enqueues it; all parsing + mbedtls verification move to `ota_task`, with
  signature rejection reported asynchronously via the status callback.

- **✅ 5.7. Companion — alias clipped by UTF-16 chars, firmware clips by bytes.**
  `Core/CompanionSettings.cs` `SanitizeAlias` limits to 32 UTF-16 chars (up to 64
  UTF-8 bytes) and `BuildHelloPayload` sets no byte limit, while the firmware does
  `strncpy(out, value, DEVICE_ALIAS_MAX=32)` (`device_config.c`). A 20–32 char
  Cyrillic/diacritic alias is cut at byte 32 mid-sequence, stored as invalid
  UTF-8 in NVS and broadcast in every IH3 HELLO — the companion then renders
  mojibake/replacement glyphs in the peer list. **Fix:** clip the alias to 32
  UTF-8 **bytes** on the companion side without splitting codepoints or surrogate
  pairs (constant equals the firmware `DEVICE_ALIAS_MAX`).

- **✅ 5.8. Firmware — data race reading `g_config` without `g_lock`.** `tx_task`
  reads `g_config.hardware_flags`, `playback_task` reads `soft_mute` and
  `speaker_volume`, all without the lock, while `device_config_apply_json` mutates
  the whole struct under `g_lock`. A `CONFIG_SET` that flips `hardware_flags` (the
  button-swap bit) arriving with a PTT edge can be read mid-update, misclassifying
  a press as broadcast/reply for one poll cycle. **Fix:** `tx_task` and
  `playback_task` snapshot the fields they need under `g_lock` once per iteration.

## 6. Cleanups from the same review (not bugs, still worth doing)

- **✅ 6a. CLAUDE.md rule violations (rules 6 & 7, ~15 files).** Raw `new Font` /
  `Color.FromArgb` in `MainForm.cs`, `DeviceCard.cs`,
  `Views/Settings/DiagnosticsPage.cs`; `AutoSize = false` on fixed-string labels
  in `Views/SettingsView.cs`, `Views/Settings/FirmwarePage.cs`. Move all colour
  and type into `UiStyles` (the only legitimate remaining `Color.FromArgb` is the
  arithmetic inside `UiKit.Blend`) and set fixed-string labels to `AutoSize`.
- **✅ 6b. Reuse / abstraction altitude.** Introduce `Peer.IsLegacy` (replaces the
  four inline `ProtocolVersion is null or 1` checks in `DeviceCard.cs` /
  `Views/DeviceGridView.cs`) and `Peer.IsOtaEligible` to unify the **divergent**
  OTA-admission checks — `Core/IntercomNode.cs` requires matching protocol version
  **and** `SupportsOta`, but the UI (`MainForm.cs`) checks only `SupportsOta`, so
  today an incompatible peer can be queued and then fails on send. Replace the
  hard-coded `"239.255.42.99:45678"` literals in `MainForm.cs` with
  `IntercomNode.MulticastGroup` + `Protocol.Port`; drop the duplicate `Blend()` in
  `DeviceCard.cs` in favour of `UiKit.Blend`. Firmware: `send_to_active_peers` and
  `send_rtp_to_active_peers` duplicate the peer-snapshot-under-lock + liveness loop
  — factor it out.
- **✅ 6c. Hot-path allocation (50 packets/s).** Remove LINQ (`Keys.Min()`,
  `Where(...).ToArray()`) from the 20 ms `JitterBuffer` playout tick, and stop
  allocating a fresh `byte[640]` per audio frame in `Audio/AudioEngine.cs` and
  `Core/ReceiveSession.cs` — reuse a buffer (safe: `BufferedWaveProvider` and
  `WaveFileWriter` copy the data before returning).

---

Source for §5–§6: external read-only review `2026-08-17-02-44-code-review.md`
(firmware + companion). Reviewer's summary: the protocol is implemented cleanly
and consistently across both sides; the concentrated risk is untrusted network
input — close 5.1–5.8 first.

Design references: `docs/design/companion-redesign-spec.md` (§2 protocol, §4
acceptance criteria), `docs/design/companion-redesign-handover.md`.
