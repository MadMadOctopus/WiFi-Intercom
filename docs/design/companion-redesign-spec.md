# Companion redesign — implementation brief

Target repo: `MadMadOctopus/WiFi-Intercom`, branch `main`.
Scope: `companion/IntercomCompanion/` (.NET 10 WinForms), `firmware/main/`, and the
shared wire protocol. This brief takes the protocol from **p1 to p2**.

Design reference (open in a browser, use the tab strip above the window):

- `Current App.dc.html` — recreation of the window as it exists today.
- `Companion Redesign.dc.html` — the target, nine screens.
- `design-exports/*.png` — one PNG per screen, same order as the tab strip.

Everything in the mock is a spec unless this document says otherwise. Where the
mock shows sample devices (`Intercom`, `Workshop`, `Old bench board`), those are
illustrative data, not fixtures to hard-code.

---

## 1. Why the redesign

Today every per-device action costs several clicks: select a row in a grid, press
*Get configuration*, edit a shared form, press *Apply configuration*. Talking to
one device means selecting its row first. Setup controls (USB provisioning, OTA)
occupy the top third of the window permanently even though they are used once per
device. The redesign moves the rare work into a Settings area and makes each
device a card that carries its own state and its own controls.

Three product requirements drive the protocol work:

1. A group holds **up to 16 devices**; the layout must stay usable at 16.
2. A device's **physical mute slider state must be visible in the companion**, and
   the companion must be able to **soft-mute a device** (device stops playing
   received audio; it keeps discovering, keeps sending, keeps floor control).
3. **Group ID and device ID become settings** instead of firmware constants.

---

## 2. Protocol changes (p2)

`Protocol.Version` becomes `2`. Keep the 32-byte big-endian `PTT1` header
unchanged. Keep audio unchanged: 16 kHz mono, 20 ms, packet-independent IMA
ADPCM, 164-byte payload, RTP PT 96 on 45679. A p2 node must interoperate with p1
nodes for audio and discovery; only the new fields are gated.

### 2.1 HELLO payload `IH3`

Add a third discovery format alongside `IH1`/`IH2`. Parsers keep accepting
`IH1`/`IH2` and the pre-0.6.2 bare-alias form.

```
"IH3" | protocol(1) | capabilities(1) | flags(1) | firmware_len(1) | firmware | alias
```

`flags` bit meanings (bit 0 is LSB):

| Bit | Name | Meaning |
| --- | --- | --- |
| 0 | `hw_muted` | physical mute slider is on |
| 1 | `soft_muted` | soft mute is set in NVS |
| 2 | `talking` | device currently owns the floor and is sending |
| 3–7 | reserved | must be written 0, must be ignored on read |

Rationale for putting mute in HELLO rather than a poll: the companion already
receives a HELLO from every device every 3 s, so mute state converges within one
discovery interval with no extra traffic and no request/response state machine.

### 2.2 Configuration fields

`CONFIG_GET` / `CONFIG_SET` / `CONFIG_REPLY` payloads and the USB line-JSON share
one field set. Add:

| Field | Type | Range | Restart | Notes |
| --- | --- | --- | --- | --- |
| `soft_mute` | bool | — | no | device stops I2S playout of received audio; discovery, sending and floor control continue |
| `hw_muted` | bool | read-only | — | reported in `CONFIG_REPLY`, never accepted in `CONFIG_SET` |
| `mesh_id` | string | exactly 4 chars, `A–Z0–9` | yes | was the `MESH` constant |
| `device_id` | uint32 | non-zero | yes | was derived in firmware |

Existing fields keep today's semantics: `alias` and `speaker_volume`
(64–1024) apply without restart; `ssid`, `password`, `led_brightness` (0–255),
`buttons_swapped` and `ring_orientation` acknowledge first, then restart.

`CONFIG_REPLY` must echo every field the device holds, including `soft_mute` and
`hw_muted`, so the companion never has to guess.

### 2.3 Firmware behaviour

- Store `mesh_id`, `device_id` and `soft_mute` in NVS next to the existing
  configuration; migrate silently on first boot of a p2 build (`mesh_id` defaults
  to the old `MESH` constant, `device_id` to the value the current build derives,
  `soft_mute` to false).
- `soft_mute` and the hardware slider are independent inputs to the same gate:
  playout is silenced when either is active. The ring keeps animating in both
  cases — the device is still part of the intercom.
- Recompute the `hw_muted` HELLO flag from the debounced slider reading, so a
  slider flick is reflected in the next beacon.
- `mesh_id` writes must be validated (length, character set) and rejected with
  the existing error path rather than storing a value that would isolate the unit.
- A `device_id` write that collides is not detectable by the device; the companion
  performs that check (§4.6). The device only rejects zero.

---

## 3. Companion — screen by screen

Window: resizable, designed at **1280 × 820**, minimum 1024 × 700. Segoe UI.
Palette (already in `MainForm.cs`): green `#2E7D32`, blue `#1565C0`, purple
`#6A1B9A`, amber `#F9A825`, red `#B22222`; surfaces `#F5F6F7` / `#FFFFFF`,
borders `#DCDFE3`, text `#17191C`, muted text `#63676D`. Flat borders, no
rounded corners, no gradients.

The existing threading contract in `docs/windows-companion-design.md` is
unchanged and still binding: the UI thread owns controls, network and audio work
stay on background tasks, an audio failure never blocks discovery or the device
list.

### 3.1 Identity strip (top, 56 px)

Companion alias, ID, group and live device count; the microphone and speaker
names with a *Change…* link into Settings; *Mute my speaker* (local playback
only, not a protocol action); *Settings…*.

### 3.2 Now bar (76 px)

Replaces today's 18 pt status label. Shows one state with its colour and a
one-line detail: `Idle`, `Claiming`, `Talking`, `Receiving`, `Floor occupied`,
`Audio unavailable`, `Network unavailable`. While receiving it names the speaker
("Intercom is speaking") and shows duration and output buffer.

### 3.3 Device grid

One card per active device, three columns, `AutoScroll` panel, cards one row
high (~120 px). Card contents, top to bottom:

1. Alias (ellipsised) and a state badge: `Speaking`, `Idle`, `Soft muted`,
   `Muted`, `Legacy`.
2. `id · ip · firmware · protocol`, small, tabular figures.
3. Mute line: a dot plus text — "Playing received audio", "Soft muted from here",
   "Mute slider on at the device", "No mute reporting before p2".
4. **Hold to talk** (purple, directed PTT to this device), **Silence** toggle,
   `⋯` overflow.
5. Volume slider writing `speaker_volume`, with the numeric value.

Rules:

- *Hold to talk* is a directed session to that device only; floor control still
  fans out to the peer snapshot exactly as today.
- *Silence* writes `soft_mute`. When the device reports `hw_muted`, the control is
  disabled with the hardware explanation — no software override of a physical
  switch. On a p1 device the control is disabled as unsupported.
- The volume slider is debounced (send on release, or 250 ms after the last
  change) and shows the last value read or written.
- `⋯` menu: *Configure…*, *Get configuration*, *Update firmware…*, *Remove*.
- The speaking card animates: a 2.2 s loop fading its border from `#DCDFE3` to
  `#1565C0` with a soft blue halo and back. Implement with one `Timer` on the
  grid stepping a phase value (~60 ms) and invalidating only the speaking card.
  Stop the timer when nothing is speaking.
- Header row: title, `n of 16 in group`, an alias/ID filter box, and filter chips
  `All`, `Speaking`, `Muted`, `Silenced`, `Legacy` with counts. Chips never wrap.

### 3.4 Known, not responding

Devices seen in an earlier session but not currently announcing, as rows below
the grid: alias, ID, last address, how long since the last HELLO, *Remove…*, plus
*Remove all*. A device that announces again moves back into the grid.

Persist the known list in `%AppData%\WiFi-Intercom\devices.json`: node ID, alias,
last address, last-seen timestamp, last-known volume/brightness/mute. Discovery
still owns liveness; this file is only the memory of aliases and settings.

Removal is local-only and the dialog must say so: it forgets this PC's record,
does not touch the device, and does not remove it from the group.

### 3.5 Right column

Full-width **Hold to broadcast** (green), then **Hold to reply** (blue) with the
last sender named. Hotkey hints under each. Below: an *Activity* list (one line
per session with time, colour dot and text), a link to the recordings folder, and
a *Diagnostics…* link.

### 3.6 Settings

Left nav, four pages. Nothing in Settings may block discovery, the device grid or
PTT.

- **Set up a device (USB)** — three-step header, port picker showing the
  identified device, SSID, password (never persisted), device alias, then *Send to
  device and reboot*.
- **Group and device IDs** — current group and companion ID, then a red danger
  block: the four consequences, new group field (4 chars, `A–Z0–9`), a
  type-the-current-group confirmation, apply-to checkboxes (this companion / all
  active devices sequentially), button disabled until the typed value matches.
- **Firmware** — verified package summary, then the update queue: one row per
  device with version transition, state, progress bar and detail, *Add all
  compatible*, *Start queue…*.
- **Identity, audio, shortcuts** — companion alias, ID, mic, speaker, broadcast
  and reply global hotkeys, run-in-notification-area and start-with-Windows.
- **Diagnostics** — four live counters (UDP audio, decoded, PLC, output buffer)
  and the session log with *Copy log* / *Save log to file…*.

### 3.7 Dialogs

- **Configure device** — alias, speaker volume, ring brightness, playback
  (plays / soft muted, with the reported hardware slider state), buttons
  standard/swapped, ring centre 0°/180°. States plainly that the edit is local
  until *Apply to device*, and which fields cause a restart. Keeps today's rule:
  discovery never overwrites an in-progress edit.
- **Remove device** — as §3.4.
- **Confirm firmware update** — package identity, the four consequences, the
  device order, and an acknowledgement checkbox that gates *Start update*. Local
  PTT is disabled while a queue runs.

---

## 4. Acceptance criteria

Each item is independently testable. "Device" means a real XIAO running the p2
firmware unless stated.

### 4.1 Protocol and firmware

1. A p2 device beacons `IH3`; a p2 companion parses `IH1`, `IH2` and `IH3`, and a
   p1 companion still lists a p2 device.
2. Moving the hardware slider changes the device's card to `Muted` within one
   discovery interval (≤ 4 s) with no user action.
3. `soft_mute: true` silences received playout within 250 ms of the ack; the
   device still appears in discovery, still wins/loses the floor, and can still
   transmit while soft-muted.
4. Slider on plus `soft_mute: false` stays silent; both off resumes playout.
5. `soft_mute` and `speaker_volume` take effect without a restart; `mesh_id`,
   `device_id`, `led_brightness`, `buttons_swapped`, `ring_orientation`, `ssid`
   and `password` acknowledge before restarting.
6. `CONFIG_REPLY` never contains a Wi-Fi password and always contains
   `soft_mute`, `hw_muted`, `mesh_id` and `device_id`.
7. `mesh_id` values that are not exactly 4 characters of `A–Z0–9` are rejected and
   the stored value is unchanged; `device_id: 0` is rejected.
8. Flashing a p2 build over a p1 device preserves alias, volume, brightness,
   buttons, orientation and Wi-Fi credentials, and reports the old group.
9. A p1 device in the group is audible both ways; its card shows `Legacy` and
   disabled Silence, and it is listed *Not eligible* in the firmware queue.

### 4.2 Device grid at scale

10. With 16 announcing devices the grid renders 16 cards, three per row, no
    horizontal scrollbar at 1280 wide, and no card content clipped or wrapped.
11. Rebuilding the grid on each 1-second refresh does not flicker, does not lose
    a slider drag in progress, and does not steal focus from the filter box.
12. A device that stops announcing leaves the grid within 10 s and appears in
    *Known, not responding* with its alias intact.
13. Filter chips and the search box narrow the grid live; counts match the cards
    they describe; chips stay on one line at the minimum window width.
14. CPU stays under 5% on a typical desktop with 16 devices and one speaker,
    including the pulse animation.

### 4.3 Talking

15. Holding a card's *Hold to talk* sends directed audio to that device only; no
    other device plays it, and its floor control still reaches the snapshot.
16. Releasing sends `END`; the card returns to `Idle`; no audio continues.
17. `Space`, `Ctrl+Alt+B` and the broadcast button are equivalent, and the global
    hotkeys work while the window is unfocused or in the notification area.
18. A press while another session owns the floor retains at most 500 ms, then
    either sends or is dropped with the *Floor occupied* state — unchanged from
    today.
19. The speaking device's card pulses only while that device holds the floor, and
    the animation stops within one frame of `END`.

### 4.4 Mute controls

20. *Silence* on a card round-trips: the button reads `Silenced`, the mute line
    reads "Soft muted from here", and both survive a companion restart because
    the device reports it.
21. *Silence* is disabled with the hardware explanation whenever the device
    reports `hw_muted`, and re-enables within one discovery interval of the
    slider going off.
22. *Mute my speaker* silences local playback only and changes nothing on any
    device.

### 4.5 Known devices and removal

23. Removing a device deletes only its `devices.json` record; the device keeps
    running, keeps its group, and reappears as a normal card when it announces.
24. *Remove all* asks once, names the count, and removes only offline records.
25. A restarted companion shows previously known devices under *Known, not
    responding* before any HELLO arrives, then promotes them as they announce.

### 4.6 Group and device IDs

26. *Change group ID* is disabled until the typed confirmation equals the current
    group; the field rejects anything other than 4 × `A–Z0–9`.
27. Changing the companion's group alone empties the device grid (old-group
    devices are no longer visible) and the danger screen said so beforehand.
28. Applying to all active devices proceeds one device at a time, reports each
    result, and continues past a device that fails.
29. Setting a `device_id` already held by another visible device is refused in the
    companion before any packet is sent.
30. After a successful group change on companion and devices, discovery, talking
    and configuration all work in the new group, and nothing leaks between the two
    groups on the same LAN.

### 4.7 OTA

31. *Start queue…* always opens the confirmation dialog; *Start update* stays
    disabled until the acknowledgement checkbox is ticked; Cancel starts nothing.
32. The queue runs strictly one device at a time, showing per-device progress,
    and only reports success when the device re-announces the offered version.
33. Local PTT and Silence controls are disabled for the duration of a queue and
    re-enabled afterwards, including after a failure.
34. A failed device rolls back, its row shows the failure with the device's
    message, and the queue continues with the next device.
35. A device without the OTA capability, or offline, is *Not eligible* with the
    reason shown, never silently skipped.

### 4.8 Setup, tray, diagnostics

36. USB setup identifies the connected device before any write, never persists
    the password, and the device appears in the grid after its reboot.
37. Closing the window keeps the app in the notification area with hotkeys live;
    the tray icon reflects `Idle` / `Talking` / `Receiving`; *Exit* stops
    discovery and audio cleanly.
38. Diagnostics counters match `ReceiveSession.Statistics` and
    `AudioEngine.BufferedMilliseconds`; *Copy log* puts the visible lines on the
    clipboard.
39. Starting with the selected audio device unavailable still shows discovery and
    the device grid, with *Audio unavailable* in the now bar.

---

## 5. Out of scope

Encryption and authentication (the group remains trusted-LAN only), recording
playback inside the app, more than one group per companion, per-device audio
routing, and any change to the audio codec, frame size or ports.

## 6. Suggested delivery order

1. Protocol p2: `IH3`, new config fields, tests for both parsers.
2. Firmware: NVS migration, `soft_mute` gate, `hw_muted` reporting, `mesh_id` /
   `device_id` validation.
3. Companion device grid, cards and known-devices store (no new dialogs yet).
4. Silence, volume, configure dialog, pulse animation.
5. Settings area: USB, group/IDs danger screen, identity, diagnostics.
6. OTA queue and its confirmation.
7. Tray and global hotkeys.
