# Companion redesign — handover to Claude Code

Branch `codex/companion-p2-redesign`, at `0935fcb`. Written 2026-08-15.

Two agent passes have been made at the companion UI redesign. Both produced a
window that is recognisably the design but wrong in the same ways, and the
second pass fixed surface defects without removing their cause. This document
explains the cause, tells you what to keep and what to delete, and lays out the
work in phases with a check at the end of each.

Read this, then `docs/design/companion-redesign-spec.md`, then open
`docs/design/prototype/Companion Redesign.dc.html` in a browser. The reviews
(`companion-ui-review-1.md`, `-2.md`) are the defect lists; `-2` is current and
its `R2-n` IDs are referenced throughout.

---

## 1. Where we are

**Working and worth keeping:**

- `Core/` — protocol, discovery, receive session, OTA, USB, known-devices store.
  Untouched by the UI problem. p2 protocol work is separate and mostly landed.
- `Audio/` — unchanged and fine.
- `UiStyles.cs` — the palette, type scale and flat-button helpers are correct
  and match the design. Keep and extend; do not duplicate its values.
- `FlatSlider.cs` — correct custom slider, matches the design's 3 px track and
  9 × 13 px handle. Use it everywhere a slider is needed.
- `DeviceCard.cs` — the best-built control in the app. Structure, states,
  colours and copy are right. It needs two changes only (see phase 3).
- `GlobalPttHotkeys.cs`, `Program.cs` — fine.

**The problem:** `MainForm.Redesign.cs` (86 KB, ~1180 lines).

It is a partial class grafted onto the legacy `MainForm.cs`. `BuildRedesign()`
calls `DetachLegacyControls()`, then `Controls.Clear()` on the form, then
re-parents surviving legacy controls (`statusLabel`, `nowAccent`, `nowTitle`,
`nowKicker`, `nowDetail`) into new containers and positions them with absolute
`Location` values. There are **14 further `Controls.Clear()` calls** on live
containers, and the settings pages are built by a shared `Page(...)` helper that
hands back a `FlowLayoutPanel` whose children are then sized by hand.

Every defect in review 2 falls out of that design:

| Symptom | Cause |
| --- | --- |
| Dashed ghost strip at the top of every settings page (shots 05–09) | a legacy control that survived `DetachLegacyControls()` and is still parented to the content host |
| Stray empty white boxes under the danger block, above the firmware queue | placeholder panels left behind by a `Controls.Clear()` + rebuild cycle |
| Danger block, firmware package panel, Activity panel render **empty** | children added to a container that was cleared or replaced after the add |
| `Ac tiv`, `Hold to`, `Set up a device (U` | hand-set `Width` / `Location` on labels instead of `AutoSize` + docking |
| Now-bar accent flush to the edge, detail line missing | `nowAccent.Location = new Point(20, 16)` and a `nowDetail` positioned from `nowBar.Width` at build time, never re-laid-out |
| Cards not filling the column | `DeviceCard` has a fixed `Size = 292 × 150` and `LayoutDeviceCards()` places them in a flow |

Patching this file has been tried twice. Do not try a third time.

**The two functional failures** (independent of layout, both acceptance
criteria): `Change group ID` is enabled without a matching typed confirmation
(spec 4.6/26), and the firmware `Start update` acknowledgement gate is not
wired (4.7/31).

---

## 2. The decision: rebuild the view layer, keep everything else

Delete `MainForm.Redesign.cs` and reduce `MainForm.cs` to a controller. Build
the window fresh, in code, as a small set of `UserControl`s — one per region,
each owning its own layout and exposing an `Update(...)` method that mutates
existing controls rather than rebuilding them.

Target file layout under `companion/IntercomCompanion/`:

```
MainForm.cs              window, wiring, timers, node/session events only — no layout
Views/ShellView.cs       root TableLayoutPanel: identity / now bar / body / status
Views/IdentityStrip.cs
Views/NowBar.cs
Views/TalkView.cs        left column + right column
Views/DeviceGridView.cs  header row, chips, filter, card grid, footnote
Views/KnownDevicesView.cs
Views/ActivityPanel.cs
Views/SettingsView.cs    nav + content host
Views/Settings/UsbPage.cs
Views/Settings/GroupIdsPage.cs
Views/Settings/FirmwarePage.cs
Views/Settings/IdentityPage.cs
Views/Settings/DiagnosticsPage.cs
Dialogs/ConfigureDeviceDialog.cs
Dialogs/RemoveDeviceDialog.cs
Dialogs/ConfirmFirmwareUpdateDialog.cs
DeviceCard.cs            keep
FlatSlider.cs            keep
UiStyles.cs              keep, extend
```

Every view is constructed exactly once, in `MainForm`'s constructor. After
construction, no control is added to or removed from any container except the
two leaf collections that genuinely vary (`DeviceGridView`'s card table and
`KnownDevicesView`'s rows), and those two rebuild only their own direct
children.

This is more deletion than writing. `MainForm.Redesign.cs` contains the right
copy strings and the right event wiring; lift those across as you go rather
than re-deriving them, but do not lift its layout code.

---

## 3. Phases

One commit per phase. Run the app at the end of each; do not start the next
phase until its check passes.

### Phase 0 — Clear the ground

1. Delete `MainForm.Redesign.cs`.
2. Strip `MainForm.cs` down to: fields for the node, session, audio engine,
   settings, known-devices store, timers; construction of the views; event
   wiring; and the handlers. Remove every control declaration and every piece of
   layout from it. If a legacy control is referenced by a `Core` event handler,
   the handler now calls a method on a view instead.
3. `Form` properties: `ClientSize = 1280 × 820`, `MinimumSize = 1024 × 700`,
   `AutoScaleMode = AutoScaleMode.Dpi`, `Font = new Font("Segoe UI", 9f)`,
   `AcceptButton = null`, `KeyPreview = true`, `DoubleBuffered = true`.

**Check:** the app builds and runs with an empty window. Nothing else.

### Phase 1 — Shell skeleton

`ShellView : UserControl`, `Dock = Fill`, one `TableLayoutPanel`, 1 column,
4 rows:

| Row | Content | RowStyle |
| --- | --- | --- |
| 0 | `IdentityStrip` | Absolute 56 |
| 1 | `NowBar` | Absolute 76 |
| 2 | `bodyHost` (Panel) | Percent 100 |
| 3 | `StatusBar` | Absolute 30 |

`bodyHost` holds exactly two children, both `Dock = Fill`, added once:
`TalkView` and `SettingsView`. Switching pages sets `Visible` on those two and
on the now bar (`NowBar.Visible = false` on Settings). Nothing else changes.

**Check:** switch Talk ↔ Settings twenty times. No artifact, no flicker, no
control outside its parent, at all three window sizes.

### Phase 2 — Geometry

Build the two page layouts, with placeholder panels for content. This phase is
only about size; no styling detail yet.

- **`TalkView`** — `TableLayoutPanel`, 1 row, 2 columns: `Percent 100` and
  **`Absolute 336`**, padding `20, 18, 20, 18`.
  Left cell: rows `AutoSize` (grid header), `Percent 100` (card grid,
  `AutoScroll`), `AutoSize` (known devices), `AutoSize` (footnote).
  Right cell: `TableLayoutPanel`, rows `AutoSize` × n, `Percent 100` on the
  activity panel row.
- **`SettingsView`** — `TableLayoutPanel`, 1 row, 2 columns: **`Absolute 236`**
  (nav) and `Percent 100` (content host). Nav items are `Label`,
  `AutoSize = false`, `Dock = Top`, `Height = 40`, `Padding = 11, 0, 18, 0`,
  `AutoEllipsis = false`. A permanent `Back to Talk` row sits above the section
  label (`adjustments/01-back-to-talk.md`). All five settings pages are
  constructed once, added to the content host once, and switched by `Visible`.
- Each settings page: `Dock = Fill`, `AutoScroll = true`,
  `Padding = 24, 24, 28 + SystemInformation.VerticalScrollBarWidth, 24`, with
  an inner stack carrying `MaximumSize` (USB 720, firmware 820, diagnostics 860,
  identity 720), left-aligned.

**Check:** every one of the five nav labels renders in full. At 1024 × 700 no
horizontal scrollbar appears on Talk. Placeholders fill their cells exactly.

### Phase 3 — Talk screen content

- **`IdentityStrip`** — state dot is a **9 px anti-aliased circle** (`R2-13`),
  20 px from the left; 1 px × 28 px `#E6E8EA` dividers between the identity, mic
  and speaker blocks (`R2-14`); `Change…` and `Settings…` links right-aligned.
- **`NowBar`** — accent bar **4 × 44 px**, vertically centred, 20 px from the
  left edge, 16 px before the text (`R2-11`). Kicker + title left; detail line
  right-aligned, 9 pt `#63676D`, 20 px from the right edge, laid out by a
  `TableLayoutPanel` so it tracks resize (`R2-12`). Detail text: idle
  `nothing on the floor · output buffer <n> ms`; receiving
  `<alias> · <duration> s · output buffer <n> ms`.
- **`DeviceGridView`** — header row (title, `n of 16 in group`, filter box
  26 × 150 `FixedSingle` with `PlaceholderText = "Alias or ID"`, chips). Chips
  are custom-painted panels; **the count inside a chip is text, not a bordered
  child control** (`R2-19`, `R2-4`). Grid is a `TableLayoutPanel`,
  `ColumnCount = 3`, three `Percent 33.33` columns, `Dock = Fill`,
  `AutoScroll`; the only container in the app allowed to rebuild its children,
  and only its own.
- **`DeviceCard`** — two changes: remove the fixed `Size`, set
  `Dock = DockStyle.Fill`, `Margin = 5`, `Height` via the row style (120);
  and switch the left accent to `#1565C0` while the card holds the floor
  (`R2-16`). The pulse animation already exists and is correct — one 60 ms timer
  on the grid stepping a phase and invalidating only the speaking card, stopped
  when nothing speaks. Keep it; a static outline is not acceptable (`R2-15`,
  spec 4.3/19).
- **`KnownDevicesView`** — inside the left column, appears as soon as a device
  is known and **silent on every group** (`R2-18`); humanised last-seen (`not
  heard for 40 minutes`), never a raw `TimeSpan`. A device announcing on a group
  the companion has not joined belongs in the *Other groups on this network*
  panel instead — see `adjustments/02-multi-group.md`, which also adds group
  sections to the grid and a broadcast-target picker to the identity strip.
- **`ActivityPanel`** — rebuild from scratch (`R2-8`, review 2 §3c): header
  (`Activity` + `Open recordings folder` link, 1 px `#EDEFF1` bottom rule),
  scrolling rows (time 46 px tabular, 8 × 8 px state square, text), and
  `Diagnostics…` in **this panel's footer**, not a separate box.
- Footnote sits 16 px under the grid content, max width 900 px (`R2-17`).

**Check:** compare against `design-exports/01-talk-full-group.png` at 100 %,
125 % and 150 % DPI. Panel edges within a few pixels. Speak from a device and
watch the card pulse start and stop.

### Phase 4 — Settings pages

Build each page fresh. The three composites that were empty in the last build
are specified in full in `companion-ui-review-2.md` §3 — follow those container
trees literally.

- **Group and device IDs** — restructured by `adjustments/02-multi-group.md`
  (joined-groups panel, per-device moves); read that before building this page.
  The danger block (§3a) keeps its chrome: header, four consequences, fields,
  button, border painted in `OnPaint` — 1 px `#B22222` rectangle plus a 4 px
  `#B22222` left bar.
  **Wire the gate**: the action button is `Enabled = false` until the
  confirmation text equals the destination group code exactly (ordinal,
  case-sensitive) and at least one device is ticked; group codes are exactly
  4 × `A–Z0–9`, uppercased on input (spec 4.6/26). Use `DisabledTintButton` from
  `UiStyles.cs`.
- **Firmware** — the package summary panel (§3b) is the widest panel on the
  page and must never render empty; show the no-package state instead. Queue
  rows per `R2-29`. `Start queue…` opens the confirmation dialog whose
  acknowledgement checkbox gates `Start update` (spec 4.7/31), and local PTT and
  Silence are disabled for the duration (4.7/33).
- **Set up a device (USB)** — `Wi-Fi network` is a 240 px `DropDownList`
  `ComboBox` of scanned SSIDs (`R2-25`); port items read
  `COM3 — Intercom (90b77b2c)` once identified; identify status is `#63676D`
  when idle, green on success, red with the reason on failure (`R2-26`); one
  panel, one border (`R2-27`).
- **Identity, audio, shortcuts** — closest page in the last build; `AutoSize`
  the panel so it does not run past its last row (`R2-32`). The action row is
  `Save` alone — the per-page `Back to Talk` button is gone, replaced by a
  permanent row in the settings nav; see `adjustments/01-back-to-talk.md`.
- **Diagnostics** — stat cards `AutoSize` from content so they are not clipped;
  the log is a `RichTextBox` with `BorderStyle = None` inside a 1 px `#101215`
  wrapper that carries the padding, so the scrollbar sits outside the visual
  padding (`R2-34`); height a whole multiple of the line height. The view shows
  `hh:mm:ss  EVENT  id  detail…`; *Copy log* and *Save log to file…* keep the
  full ISO timestamp (`R2-35`).

**Check:** each of the five pages against its export. No empty panel anywhere.
Type a wrong confirmation, then the right one, and watch the button enable.

### Phase 5 — Dialogs

- **Configure device** (`R2-20`–`R2-24`, plus the `Group` row from
  `adjustments/02-multi-group.md`): one label column 150 px + one control
  column; alias 240 px; volume and brightness are `FlatSlider` + a tabular value
  cell (**no `NumericUpDown` anywhere in this app**); `Device ID` a flat digits-
  only `TextBox`, non-zero; a **Playback** row showing plays / soft muted with
  the reported hardware slider state as read-only text; a `restarts the device`
  note after each restart-causing field; `Apply to device` (primary) + `Cancel`,
  right-aligned under a 1 px `#EDEFF1` separator; `FixedDialog`, no minimise or
  maximise, `ShowInTaskbar = false`, 16.5 pt Bold title.
- **Remove device**: says plainly that removal is local-only — it forgets this
  PC's record, does not touch the device, does not remove it from the group
  (spec 4.5/23).
- **Confirm firmware update**: package identity, four consequences, device
  order, acknowledgement checkbox gating `Start update`.
- Card overflow menu: `ContextMenuStrip` with a flat
  `ToolStripProfessionalRenderer` colour table — white, 1 px `#DCDFE3`, no
  gradient, no margin gutter, hover `#F2F4F5`. Items unchanged.

### Phase 6 — Audit

- Grep the solution for `NumericUpDown`, `TrackBar`, `ProgressBar`, `Fixed3D`,
  `SystemColors.`, `UseVisualStyleBackColor = true`, `.Location =`,
  `Controls.Clear`. Every hit outside the two allowed leaf containers is a
  defect.
- Re-read every visible string against the prototype. Now-bar kicker uses the
  state word set (`IDLE`, `CLAIMING`, `TALKING`, `RECEIVING`, `FLOOR OCCUPIED`,
  `AUDIO UNAVAILABLE`, `NETWORK UNAVAILABLE`). Counts are spelled or
  parenthesised, never `device(s)`.
- Walk acceptance criteria 10–14, 19, 20–22, 26, 31, 33 by hand.

---

## 4. Definition of done

1. At 1024 × 700, 1280 × 820 and maximised: no clipped string, no overlapping
   controls, no control outside its parent, no empty or stub panel, no stray
   rule or box.
2. Switching all five settings pages and back to Talk, twice, leaves no ghost.
3. Every panel renders its full designed content, including empty states.
4. `Change group ID` disabled until the typed confirmation matches (4.6/26);
   `Start update` disabled until acknowledged (4.7/31).
5. The speaking card animates and stops within one frame of `END` (4.3/19).
6. No stock control chrome in the content area (rule 5 in `CLAUDE.md`).
7. Screenshots at 100 %, 125 % and 150 % DPI overlay the matching
   `design-exports/*.png` with panel edges within a few pixels.

## 5. What is in this handover

```
CLAUDE.md                                    standing rules for the repo
docs/design/companion-redesign-handover.md   this file
docs/design/companion-redesign-spec.md       behaviour + protocol + criteria 1–39
docs/design/companion-ui-review-1.md         first build review
docs/design/companion-ui-review-2.md         second build review (current, R2-n IDs)
docs/design/logo-integration.md              brand asset placement
docs/design/adjustments/                     design changes made after review 2 —
                                             read these with the phase they touch
docs/design/prototype/                       the interactive prototype, nine screens
docs/design/prototype/design-exports/        one PNG per screen
docs/design/build-shots/round-1/             screenshots of the first build
docs/design/build-shots/round-2/             screenshots of the second build
```

Open the prototype by opening `Companion Redesign.dc.html` in a browser;
`support.js` sits beside it and must stay there.
