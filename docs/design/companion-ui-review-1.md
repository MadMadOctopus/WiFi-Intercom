# Companion UI — design review of the current build

Reviewed against `Companion Redesign.dc.html` (the design of record) and
`docs/companion-redesign-spec.md`. Screenshots reviewed:
`docs/review-shots/01-talk-idle.png` … `07-talk-receiving.png`.

The functionality is largely there. What is wrong is almost entirely **layout
strategy and control choice**: absolute/`Location`-based placement instead of
docked layout panels, stock WinForms controls left at their system look, and
containers sized smaller than their content so labels and buttons clip or
overlap. Fix the six global rules in §1 first — they remove roughly two thirds of
the individual defects below.

Every issue has an ID (`G-n`, `T-n`, `U-n`, …). Please reference the IDs in the
commit messages so the next review can be diffed.

---

## 1. Global rules (fix these first)

### G-1 · Stop positioning controls by hand

Multiple screens show controls overlapping each other or sitting outside their
parent panel (`F-1`, `F-3`, `T-19`, `T-24`). That only happens with manual
`Location`/`Size`. Rebuild every screen out of nested containers:

- Page shell: `TableLayoutPanel`, one column, rows = title bar / identity strip /
  now bar / body / status bar. Body row `SizeType.Percent = 100`.
- Body: `TableLayoutPanel`, two columns — `Percent 100` and `Absolute 336`.
- Forms (`USB`, `Group`, `Identity`, `Configure`): `TableLayoutPanel`,
  2 columns (`Absolute` label column, `Percent 100` control column), one row per
  field, `AutoSize = true`, `RowStyles = AutoSize`.
- Card grid: `FlowLayoutPanel` (`AutoScroll = true`, `WrapContents = true`)
  or a 3-column `TableLayoutPanel` rebuilt on refresh.
- Rows of buttons: right-docked `FlowLayoutPanel` with
  `FlowDirection.RightToLeft`, never two buttons at hand-picked coordinates.
- Every panel: set `Padding`, never fake it with spacer labels.

### G-2 · No control may clip its own text

Current clipping: `Silenc`, `Space /`, `Ctrl+`, `Remove all 1…`,
`Set up a device (US`, `Group and device I`, `Identity, audio, sho`,
`ackage...`. Rules:

- Buttons: `AutoSize = true`, `AutoSizeMode = GrowAndShrink`, then add
  `Padding` — do not hard-code widths.
- Labels that must not wrap: `AutoSize = true`, `AutoEllipsis = false`.
- Only two strings in the whole app are allowed to ellipsise: the device alias on
  a card and the audio device names in the identity strip.
- Give the settings nav an absolute width of **236 px** (it is 132 px today) and
  the right column **336 px** (240 today).

### G-3 · Flatten the stock controls

The design has flat 1 px borders, no 3-D chrome, no rounded corners, no system
accent. Today buttons, text boxes, combos, the track bar and the log box are all
default WinForms.

| Control | Required setup |
| --- | --- |
| `Button` | `FlatStyle = Flat`, `FlatAppearance.BorderColor = #ADB2B8`, `BackColor = #FFFFFF`, `UseVisualStyleBackColor = false`, `TabStop` kept, `FlatAppearance.MouseOverBackColor = #F2F4F5` |
| Primary `Button` | `BackColor` = accent, `ForeColor = #FFFFFF`, `FlatAppearance.BorderColor` = same accent, bold |
| Disabled primary | tinted accent (`#D08A8A` red, `#8FB3D9` blue) with white text — not the grey system disabled look |
| `TextBox` | `BorderStyle = FixedSingle` (never `Fixed3D`), height set via `Font` + `Padding` on a wrapper panel, target 26 px |
| `ComboBox` | `DropDownStyle = DropDownList`, `FlatStyle = Flat` |
| Read-only values | `Label`, never a disabled `TextBox` (see `F-1`) |
| Volume / brightness slider | **custom-drawn**, not `TrackBar` (see `T-16`) |
| Filter chips | custom `Panel` + `Label` pair, not `Button` (see `T-8`) |
| Log view | `RichTextBox`, `BorderStyle = None`, `BackColor = #1C1F23`, per-line colour (see `D-2`) |
| Progress | flat filled `Panel`, not `ProgressBar` |

Also: no control in the design has a **default-button focus ring**. `Settings…`
currently renders as the form's `AcceptButton` (dark 2 px outline). Set
`Form.AcceptButton = null` on the Talk screen, or point it at the broadcast
button without the visual.

### G-4 · Type scale — the design is in px, WinForms is in points

Convert once, put it in a static `Fonts` class, and use nothing else. (96 DPI:
`pt = px × 0.75`.)

| Role | Design px / weight | WinForms |
| --- | --- | --- |
| Now-bar title | 26 / 700 | Segoe UI **19.5 pt Bold** |
| Page heading (Settings) | 20 / 700 | 15 pt Bold |
| Broadcast button | 17 / 700 | 12.75 pt Bold |
| Panel heading, dialog title, stat value | 15 / 700, 22 / 700 | 11.25 pt Bold, 16.5 pt Bold |
| Card alias, device name | 14 / 700 | 10.5 pt Bold |
| Body, form labels, inputs | 13 / 400 | 9.75 pt |
| Secondary text, buttons, table cells | 12 / 400 | 9 pt |
| Now-bar kicker, card meta, hints, section label | 11 / 400 | 8.25 pt |
| Card meta / mute line / volume value | 10 / 400 | 7.5 pt |
| State badge | 9 / 700 caps | 6.75 pt Bold |
| Log | 12 / 1.7 Consolas | 9 pt Consolas |

Set `Form.AutoScaleMode = AutoScaleMode.Dpi` and verify at 125 % and 150 %.

### G-5 · Colours

Only these values, from the design: green `#2E7D32`, blue `#1565C0`,
deep blue `#0D47A1`, purple `#6A1B9A`, amber `#F9A825`, red `#B22222`,
dark red text `#8E1A1A`; surfaces `#F5F6F7`, `#FFFFFF`, `#EEF0F2`, tint
`#EEF4FB`, red tint `#FDF3F3`, amber tint `#FDF6E3`; borders `#DCDFE3`,
`#ADB2B8` (control), `#7A7F85` (input), `#EDEFF1` / `#F2F4F5` (row rules);
text `#17191C`, `#3D4147`, `#63676D`, `#83878D`, `#9AA0A6`.
No `SystemColors.Control` grey anywhere in the content area.

### G-6 · Copy comes from the design verbatim

Several strings were paraphrased or left in developer form. Format numbers for
humans, and never show a raw .NET `ToString()`:

| Now | Required |
| --- | --- |
| `last seen 0:40:01.8426578 ago` | `not heard for 40 minutes` (`< 90 s` → `just now`, then minutes / hours / days) |
| `1 device(s) kept from earlier sessions — they return when they announce` | `3 devices kept from earlier sessions — they return to the grid as soon as they announce` (pluralise properly, never `device(s)`) |
| `Remove all 1…` | `Remove all three…` — spell the count, or `Remove all (3)…` |
| `Current group: MESH · 2 active device(s) · companion ID 391e0338` | value cell is `MESH · 2 devices, 1 companion` (the label already says "Current group"; the companion ID has its own row) |
| `All active devices, sequentially` | `All 2 active devices, one at a time (each reboots)` |
| `Run in notification area when closed` | `Keep running in the notification area when closed` |
| `Device label` | `Device alias` (matches the protocol field and the rest of the UI) |
| `READY` kicker at idle | kicker is the state word set: `IDLE`, `CLAIMING`, `TALKING`, `RECEIVING`, `FLOOR OCCUPIED`, `AUDIO UNAVAILABLE`, `NETWORK UNAVAILABLE` |

---

## 2. Talk screen — `01-talk-idle.png`, `07-talk-receiving.png`

### Identity strip (56 px)

- **T-1** The status dot is missing; the app repeats the app icon at the far
  left instead. Draw a **9 px circle**, colour = state (green idle / blue
  receiving / purple talking / amber degraded), 20 px from the left edge, then a
  10 px gap before the alias block.
- **T-2** Strip left padding is ~28 px and inconsistent with the 20 px used by
  the rest of the window. All top-level horizontal padding is **20 px**.
- **T-3** Missing the 1 px × 28 px `#E6E8EA` divider between the identity block
  and the audio block.
- **T-4** Mic/speaker names are truncated at ~130 px (`Microphone (FIFINE M…`).
  Show the short device name — strip the leading `Microphone (` / trailing
  `)` wrapper — and give each column 180 px. Kicker labels `MIC` / `SPEAKER`
  are correct (11 px caps, `#83878D`, `0.08em` tracking) but sit 4 px too high;
  line-height inside the block is 1.25.
- **T-5** `Change…` is a link 60 px further right than the design; it belongs
  22 px after the speaker column, in the same flow.
- **T-6** `Mute my speaker` / `Settings…` are stock buttons. Flat, white,
  1 px `#ADB2B8`, 9 pt, padding 6/12, 8 px gap, right-aligned at 20 px from the
  edge. `Settings…` must lose the default-button outline (`G-3`).

### Now bar (76 px)

- **T-7** At `Idle` the bar keeps the *receiving* blue tint `#EEF4FB`. The tint
  is state-driven: `Idle` → `#F5F6F7` with green accent; `Receiving` →
  `#EEF4FB` + `#1565C0`; `Talking` → `#F3ECF7` + `#6A1B9A`;
  `Floor occupied` / degraded → `#FDF6E3` + `#F9A825`; failure → `#FDF3F3` +
  `#B22222`.
- **T-8** The 4 px accent bar is flush against the window edge and full-height.
  It is **4 × 44 px**, vertically centred, with 20 px to its left and 16 px
  between it and the text block.
- **T-9** The right-aligned detail line is missing entirely
  (`broadcast to the group · 3.2 s · output buffer 96 ms`). Right-align it,
  9 pt `#63676D`, line-height 1.5, 20 px from the edge. At `Idle` it reads
  `nothing on the floor · output buffer <n> ms`.

### Device grid header — currently absent

- **T-10** Missing the whole header row: `Devices` (10.5 pt Bold), then
  `n of 16 in group MESH` (9 pt `#63676D`), then right-aligned: the alias/ID
  search box (26 × 150 px, placeholder `Alias or ID`) and the filter chips.
- **T-11** Only three chips exist (`All`, `Speaking`, `Muted`); the set is
  **All, Speaking, Muted, Silenced, Legacy**, each with its count in
  `#83878D` after the label, tabular figures.
- **T-12** Chips are stock buttons floating above the right column. They are
  26 px tall flat chips (`padding 0 9px`, 5 px label→count gap, 6 px between
  chips) inside the grid header row, and they must never wrap — put them in a
  no-wrap `FlowLayoutPanel` with `AutoSize`.
- **T-13** The active chip is inverted (`#17191C` bg, white text), not the
  system-highlight look.

### Device cards

- **T-14** The grid does not fill its column: two ~300 px cards sit at the left
  with ~600 px of empty space. It is a **3-column grid, `1fr` each, 10 px gap**,
  filling the left column (≈ 900 px at 1280 wide → ~293 px cards). With two
  devices you get two cards of that width in the first two columns.
- **T-15** Cards are missing the **3 px left accent border** in the state colour
  (`#2E7D32` idle, `#1565C0` speaking, `#3D4147` soft-muted, `#F9A825`
  muted/legacy). Card border is 1 px `#DCDFE3`, padding 10/12, no radius.
- **T-16** The volume row **overflows the card**: the `TrackBar` and the `512`
  label are drawn outside/over the card border, card 2's bottom border is cut,
  and card height is not derived from content. Replace the `TrackBar` with the
  designed slider: `Vol` label (7.5 pt `#83878D`), 3 px `#E2E5E8` track filled to
  the value in the accent colour, a **9 × 13 px white handle with a 1 px
  `#7A7F85` border**, then the value right-aligned in a 34 px tabular cell. No
  tick marks, no system thumb. Above it, a 1 px `#EDEFF1` rule with 9 px above
  and 8 px below. Card height ≈ 120 px, fixed, one row.
- **T-17** The state badge overlaps the card's right border and is ~2 px too
  tall. Badge: 6.75 pt Bold caps white on the accent, padding 2/6, right-aligned
  in the alias row, 8 px minimum gap from the alias, which ellipsises.
- **T-18** The mute line uses a text bullet `•`. Draw a **6 × 6 px filled
  square** (green / `#3D4147` / amber / `#C9CCD0`) with a 5 px gap, then the
  note at 7.5 pt in the matching text colour.
- **T-19** `Silence` clips to `Silenc`, and the three buttons have different
  heights. Row: `Hold to talk` fills the remaining width (purple `#6A1B9A`,
  9 pt Bold white, padding 7/6), `Silence` auto-sized flat, `⋯` a 26 × 27 px
  flat button — all three the same height, 5 px gaps. Use the `⋯` character
  (U+22EF), not three periods.
- **T-20** Toggled `Silence` must invert (`#3D4147` bg, white text, label
  `Silenced`); hardware-muted must be `#F2F4F5` / `#9AA0A6` and disabled.
- **T-21** The speaking card in `07` is outlined by a static 2 px blue rectangle
  drawn outside the card bounds (double border, and it does not follow the card
  edge). The design pulses the card's **own** 1 px border between `#DCDFE3` and
  `#1565C0` with a soft blue halo, 2.2 s loop, plus the blue left accent — one
  60 ms timer invalidating only that card, stopped when nothing is speaking.
- **T-22** The explanatory footnote under the grid is missing (8.25 pt
  `#83878D`, max 900 px): the 16-device/scroll note and the definition of
  Silence vs the hardware slider.

### Known, not responding

- **T-23** It is a full-bleed strip at the bottom of the window. It belongs
  **inside the left content column** as a white panel, 1 px `#DCDFE3`, 16 px
  above the grid, with a header row (title 9.75 pt Bold, description 9 pt
  `#63676D`, `Remove all…` right-aligned flat button, padding 11/14, 1 px
  `#EDEFF1` bottom rule) and one 9/14 row per device separated by `#F2F4F5`.
- **T-24** `Remove all 1…` and `Remove…` **overlap each other** and are clipped
  by the window's bottom edge — the panel is placed below the client area. Fix
  by docking it in the content flow (`G-1`).
- **T-25** Row columns don't line up with the design: marker 6 px square, alias
  150 px (9 pt Semibold), address 210 px (`#63676D`, tabular), then the
  humanised last-seen (`#83878D`), `Remove…` right-aligned.
- **T-26** Row copy and the `TimeSpan` formatting — see `G-6`.

### Right column (336 px)

- **T-27** The column is ~240 px and flush to the window's right edge, so its
  panels are clipped and two hint lines truncate to `Space /` and `Ctrl+`.
  Width 336 px, 20 px right padding, 18 px top, 12 px between panels.
- **T-28** `Hold to broadcast` is not full-width and is too short. Full width of
  the panel's inner box, 12.75 pt Bold white on `#2E7D32`, 22 px vertical
  padding. `Hold to reply`: full width, 10.5 pt Bold on `#1565C0`, 14 px
  vertical padding, 12 px below the hint row.
- **T-29** Hint rows are one line, space-between: `Everyone in group MESH` /
  `Space, or Ctrl+Alt+B anywhere`, and `Last sender: <alias>` /
  `Ctrl+Alt+R`, 8.25 pt `#63676D`, 8 px under each button.
- **T-30** The two buttons live in one white panel with 16 px padding — the app
  currently has them in a panel with unequal insets and a stray internal border
  line above `Diagnostics…`.
- **T-31** Activity rows have no colour dot. Row: time in a 46 px tabular cell
  (`#83878D`), an 8 × 8 px state-coloured square, then the text (`#3D4147`),
  padding 9/16, 1 px `#F2F4F5` rule between rows.
- **T-32** `Open recordings folder` belongs in the **Activity panel header**,
  right-aligned next to the `Activity` title; `Diagnostics…` is the single link
  in a 10/16 footer under the last row. Today both are stacked at the bottom in
  the wrong order.
- **T-33** The activity list has no fixed height and leaves a large white void.
  Cap it at 6 rows and scroll.

### Status bar

- **T-34** The 30 px status bar is missing: `#EEF0F2`, 1 px `#DCDFE3` top
  border, 8.25 pt `#63676D`, 16 px padding —
  `Discovery: listening on 239.255.42.99:45678 · audio 16 kHz mono ADPCM · UDP n
  · PLC n · buffer n ms`, and right-aligned
  `Running in the notification area · Ctrl+Alt+B to broadcast`.

---

## 3. Settings shell — all settings screenshots

- **S-1** The Now bar stays visible on Settings pages. In the design Settings
  replaces everything below the identity strip; the now bar is Talk-only.
- **S-2** There is a rendering artifact at the top of the content area on every
  settings screen (a clipped grey line / ghost text at ~y = 165). Something from
  the previous page is not being disposed or is drawn outside its parent.
- **S-3** Nav width 132 px → **236 px**; all five labels then fit
  (`G-2`).
- **S-4** Nav items: padding 11/18, 9.75 pt, active = white background +
  `#17191C` text + Bold + 3 px `#1565C0` left mark; inactive = transparent +
  `#3D4147`. Today the active item is a raised white block with a border on all
  sides and the item heights vary (48 px in `04`, 44 px in `02`).
- **S-5** The section label `SETTINGS` is correct in style but needs
  `0 18px 10px` padding and `#83878D`.
- **S-6** Missing the nav footer note: *"Settings are rare. Nothing here blocks
  discovery, talking or the device list."* (8.25 pt `#83878D`, 18 px padding,
  line-height 1.6).
- **S-7** Content padding is 24/28 in the design; the app uses ~36 left and
  varies per page. Page heading 15 pt Bold, sub-line 9.75 pt `#63676D` 6 px
  under it, first panel 18 px below.
- **S-8** Panels stretch to the bottom of the window with large empty areas
  (`02`, `04`, `05`). Panels are `AutoSize` — they hug their content; the page,
  not the panel, takes the remaining space.
- **S-9** A vertical scrollbar overlaps the content on the right on `02`–`06`.
  Reserve its width, or set `AutoScroll` on the content panel only, with
  `Padding.Right` accounting for `SystemInformation.VerticalScrollBarWidth`.

---

## 4. Set up a device over USB — `02-settings-usb.png`

- **U-1** Step 1's kicker should be green `#2E7D32` (done), step 2 blue on
  `#EEF4FB` (done), **step 3 grey text `#83878D` on `#F5F6F7`** — currently
  white/dark like step 1. Steps are three equal flex cells with shared 1 px
  borders (`border-right: 0` on each, one closing 1 px line), padding 12/14.
- **U-2** The step strip spans only the panel width; it must span the same
  720 px content width as the form panel (it currently ends at 880 while the
  panel ends at 880 — align both to 720 max-width per the design).
- **U-3** Label column is ~180 px; the design is **150 px**, row gaps 14 px
  vertical / 16 px horizontal, `align-items: center`.
- **U-4** `Device identified` is a bare label on its own row with no value. It
  is **inline status text next to Rescan**, 9 pt `#2E7D32`:
  `Device identified over USB` (and `#B22222` + reason when it fails).
- **U-5** The `Identify` button is not in the design. Either remove it or fold
  it into `Rescan`; do not add controls the design does not have.
- **U-6** `Wi-Fi network (SSID)` is a plain text box; the design uses a
  **240 px combo of scanned SSIDs** with the hint `2.4 GHz only` beside it.
  Label text is `Wi-Fi network`.
- **U-7** `Wi-Fi password` has no `Show` toggle. Masked `TextBox` 240 px +
  flat `Show` button, 8 px gap.
- **U-8** `Device label` → `Device alias`, 240 px.
- **U-9** All three inputs are ~150 px and use the sunken 3-D border. 240 px,
  26 px tall, `FixedSingle` 1 px `#7A7F85` (`G-3`).
- **U-10** The USB port combo is ~285 px and shows only `COM3`. 280 px, and the
  item text is `COM3 — Intercom (90b77b2c)` once identified.
- **U-11** The primary button row is missing its **1 px `#EDEFF1` separator**
  (20 px above, 16 px below) and the helper text belongs **to the right of the
  button**, 12 px gap, 9 pt `#63676D`:
  *"The device restarts and should appear in Devices within a few seconds."*
  The current helper line under the button repeats the sub-heading.
- **U-12** Button padding 10/18, 9.75 pt Bold — currently ~10/24 and regular
  weight.

---

## 5. Group and device IDs — `03-settings-group.png`

- **P-1** The value cell repeats the label (`G-6`).
- **P-2** `This companion's ID` renders in link blue. It is body text
  `#63676D`, 9.75 pt, tabular:
  `391e0338 · generated once, unique on this PC`.
- **P-3** Label column 170 px; the app's is ~185 px and the two rows have
  uneven vertical gaps.
- **P-4** The danger block uses a uniform 2 px red border. It is **1 px
  `#B22222` + a 4 px `#B22222` left edge**, background `#FDF3F3`.
- **P-5** Its header (title 11.25 pt Bold `#8E1A1A`, sub-line 9 pt `#7A2020`,
  padding 14/18) must be separated from the body by a **1 px `#F2D6D6` rule**.
  Today it is one undivided block.
- **P-6** The four consequences are 9.75 pt `#3D4147`, line-height 1.8, with
  the group name bold in the first line. Today they are 9 pt grey and shorter —
  restore the full sentences from the design, including *"Each device must be
  changed separately, over USB or with a configuration write, and each one
  reboots."*
- **P-7** `New group ID` input is ~90 px; it is **140 px with `0.14em`
  letter-spacing**, and the hint `exactly 4 characters, A–Z and 0–9` sits 10 px
  to its right at 9 pt `#63676D`.
- **P-8** Second checkbox copy (`G-6`) — it must state that each device reboots.
- **P-9** **`Change group ID` is enabled.** Acceptance criterion 26: it stays
  disabled until the typed confirmation equals the current group. Disabled style
  is `#D08A8A` with white text, plus the trailing hint *"Enabled once the
  confirmation matches."*; enabled is `#B22222`.
- **P-10** The button colour in the build is a darker maroon than `#B22222`.
- **P-11** Danger-block padding: 16/18 body, not ~20/24.
- **P-12** The `Device IDs` panel is cut off at the window edge with no scroll
  reaching it — see `S-9`.

---

## 6. Firmware — `04-settings-firmware.png` (most broken screen)

- **F-1** `Signed firmware package` is **overlapped by a control showing
  `ackage...`** — a button or text box placed on top of the label with a width
  smaller than its text. Rebuild the panel: a left block with
  `wifi_intercom 0.7.9` (11.25 pt Bold) and, 3 px under it,
  `wifi_intercom-0.7.9.ota.json · 812.4 KiB · signature verified`
  (9 pt `#63676D`), then a right-aligned flat
  `Choose another package…` button. Panel padding 18/20.
- **F-2** The verified-package summary (version, filename, size, signature
  state) is entirely missing. It is the whole point of the panel.
- **F-3** The queue panel has **no header row** and instead shows two empty
  bordered boxes and two floating blue bars at the right — controls with no text
  and wrong bounds. Header: `Update queue` (9.75 pt Bold),
  `sequential, one device at a time` (9 pt `#63676D`, 10 px gap), then
  right-aligned `Add all compatible` (flat) and `Start queue…` (primary blue,
  9 pt Bold, padding 7/14, 8 px gap), padding 14/20, 1 px `#EDEFF1` bottom rule.
- **F-4** Queue rows are missing their designed shape: alias 10.5 pt Semibold in
  a 150 px cell, version transition in a 170 px cell, state in the state colour,
  right-aligned per-row action button; then a **6 px flat progress bar**
  (`#E9EBEE` track, state-coloured fill) 10 px below, then the detail line at
  8.25 pt `#83878D`. Use a `Panel`, not `ProgressBar`.
- **F-5** The empty state is placed centred-ish in a full-height panel. Keep the
  copy, put it in a 14/20 row, and let the panel hug its content (`S-8`).
- **F-6** Missing footer note: *"Keep the companion open. Windows may ask once
  to allow the temporary local firmware server on a private network."*
- **F-7** Content max-width 820 px on this page.

---

## 7. Identity, audio and shortcuts — `05-settings-identity.png`

- **I-1** Alias input is ~330 px → **260 px**; both combos ~390 px →
  **320 px**; hotkey fields **130 px**. Everything at 26 px tall, flat
  `#7A7F85` border, `DropDownList` + `FlatStyle.Flat` combos.
- **I-2** `Companion ID` value is body text `#63676D` (`391e0338 · fixed for
  this PC`) — do not render it as an input or a link.
- **I-3** The broadcast hotkey field is missing its inline hint *"works when the
  window is not focused"* (9 pt `#63676D`, 8 px gap).
- **I-4** The two checkboxes have no row label. The design labels the row
  `Window` in the label column and stacks the checkboxes with an 8 px gap in the
  control column, aligned to the same left edge as the inputs — today they are
  indented differently from every other control.
- **I-5** Checkbox copy (`G-6`).
- **I-6** `Save` sits inside the panel and `Back to Talk` is ~40 px below it and
  further left. Both belong **below the panel**, 16 px under it, 8 px apart:
  `Save` primary blue (10/18, 9.75 pt Bold), `Back to Talk` flat white.
- **I-7** The hotkey helper paragraph wraps to two lines under the Save button
  in a place the design has no text. If it stays, it goes with the hotkey rows
  (`I-3`), not in the button row.
- **I-8** Label column 170 px, row gaps 14/16 — currently the rows are ~29 px
  apart with drifting baselines (`Microphone` / `Speaker` labels sit ~2 px above
  their combos).
- **I-9** The intro line *"Changes to audio devices are applied independently
  from discovery and the device grid."* is invented copy. Either drop it or use
  the design's own sub-line style and keep it to one truthful sentence.

---

## 8. Diagnostics — `06-settings-diagnostics.png`

- **D-1** Stat cards: padding 14/16 (currently ~16/20), label 8.25 pt caps
  `#83878D` with `0.08em` tracking, value 16.5 pt Bold tabular 6 px under it,
  4 equal columns with a 12 px gap. Close, but the values sit too low in the
  card and the cards are 8 px too tall.
- **D-2** The log is a green-on-black terminal. It is `#1C1F23` with a 1 px
  `#101215` border, padding 14/16, 9 pt Consolas at line-height 1.7, and
  **per-line colour**: `#9AA0A6` normal, `#64B5F6` session/audio, `#FFD54F`
  warning (`BUSY`, `END`, hardware mute), `#EF9A9A` error/expiry.
  Implement with `RichTextBox` (`BorderStyle = None`, `ReadOnly`,
  `DetectUrls = false`) appending coloured lines, or a custom-painted list.
- **D-3** Log height 300 px fixed (it currently grows to ~420 px), and the
  scrollbar must not sit inside the padding.
- **D-4** Line format in the design is
  `hh:mm:ss  EVENT  id  detail…` with two spaces after the timestamp and the
  event keyword in caps. The build prints full ISO timestamps with offsets
  (`2026-08-12T00:01:34.5608652+01:00`) which pushes the useful part off the
  right edge. Show `hh:mm:ss` in the view; keep the full timestamp in
  *Copy log* / *Save log to file…*.
- **D-5** `Copy log` / `Save log to file…`: flat, padding 8/14, 9 pt, 8 px
  gap, 12 px under the log.
- **D-6** Content max-width 860 px.

---

## 9. Receiving state — `07-talk-receiving.png`

- **R-1** Kicker/title/colour are right. The right-hand detail line is still
  missing (`T-9`) — while receiving it must show speaker duration and output
  buffer.
- **R-2** The speaking card highlight (`T-21`).
- **R-3** The badge reads `SPEAKING` in blue correctly, but the card keeps the
  green left accent absent and its mute line still reads *"Playing received
  audio"* for the **speaking** device — while a device holds the floor its line
  should describe the session, not playback.
- **R-4** Activity gained a `Receiving` row but with no colour dot (`T-31`) and
  no separator rules.
- **R-5** The identity-strip status dot (`T-1`) should turn blue in this state,
  and the tray icon likewise (spec §4.8/37).

---

## 10. Suggested order of work

1. `G-1`, `G-2`, `G-3`, `G-4`, `G-5` — layout containers, control styling,
   fonts, palette. Do this before touching individual screens.
2. Talk screen structure: `T-14`, `T-16`, `T-23`, `T-24`, `T-27`, `T-34`.
3. Talk screen detail: `T-1`–`T-13`, `T-15`, `T-17`–`T-22`, `T-25`–`T-33`.
4. Firmware page rebuild: `F-1`–`F-7`.
5. Settings shell: `S-1`–`S-9`; then `U-*`, `P-*`, `I-*`, `D-*`.
6. Copy pass: `G-6`, and re-read every string against the design.
7. `P-9` — the disabled-until-confirmed gate is an acceptance criterion, not a
   nicety.

### Definition of done for this pass

- At 1280 × 820 no string is clipped anywhere except the two allowed
  ellipsised ones, and no two controls overlap.
- Resizing to 1024 × 700 and to full screen keeps the two-column split, the
  336 px right column, the 3-up card grid and the chip row on one line.
- A screenshot of each screen at 100 %, 125 % and 150 % DPI can be laid over the
  corresponding `design-exports/*.png` with panel edges within a few pixels.
- No `SystemColors` grey, no 3-D borders, no default-button focus ring, no stock
  `TrackBar` or `ProgressBar` in the content area.
