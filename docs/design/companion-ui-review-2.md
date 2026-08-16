# Companion UI — review 2 (post-Codex pass)

Reviewed against `Companion Redesign.dc.html`, `design-exports/*.png` and
`docs/companion-redesign-spec.md`. Screenshots: `docs/review-shots-2/01…09`.
Supersedes `docs/companion-ui-review.md`; issue IDs from that document are
referenced where the defect is unchanged.

**Read this whole file before writing code, then work the phases in order.**
Do not jump ahead: phases 1–3 change the geometry every later phase depends on,
and half the remaining defects disappear when they land. Each phase ends with a
check you must be able to pass before starting the next one. Do not start
several phases in the same commit.

---

## 0. What improved, and what the remaining failures have in common

Real progress since review 1: the grid header row, filter chips, status bar,
identity-strip copy, the settings nav footer note, the USB step strip, the
identity page layout, diagnostics stat cards and the coloured log all landed,
the now bar is correctly Talk-only, and the receiving card now says "Holding the
floor" instead of "Playing received audio".

What is left is not a long tail of small polish. It is **four repeating
structural faults**, and every remaining screenshot defect is an instance of one
of them:

1. **Composite panels are being built and then left empty or stubbed.** The
   danger block on *Group and device IDs* is a red rectangle with nothing in it
   (screenshot 06). The firmware package summary is a 25 × 65 px empty white box
   (07). The Activity panel is an empty box with the title collapsed to `Ac tiv`
   (01, 02). In all three cases a container exists at roughly the right place
   and its children are missing, zero-sized, or added to the wrong parent.
2. **Widths are still hand-picked and smaller than the content.** Settings nav
   is still ~135 px (`Set up a device (U`, `Group and device`, `Identity, audio,
   sh`), the right column is ~200 px (`Hold to broadcast` renders as `Hold to`),
   the Activity title has no width at all.
3. **Panels are placed rather than docked, so one page's controls bleed into
   the next.** The dashed ghost strip at the top of every settings content area
   (05–09) and the stray white 40 × 60 px box under the danger block (06) and
   above the firmware queue (07) are leftovers of that.
4. **Stock control chrome survives in the places that got rebuilt last.**
   Sunken 3-D text boxes and `NumericUpDown` spinners in the Configure dialog
   (04) and the USB form (05), a bordered box around every chip count (01), a
   system scrollbar drawn inside the log padding (09).

Where I say **rebuild**, delete the existing designer code for that region and
write it fresh as described. Patching the current code for the danger block, the
firmware package panel, the Activity panel and the right column will cost more
than replacing them.

---

## Phase 1 — One layout skeleton, docked, for the whole window

Nothing else is worth doing until this is true. Do not touch any visual detail
in this phase.

**R2-1 · Build the shell once, in code, top to bottom.**

```
MainForm (1280 × 820, min 1024 × 700, AutoScaleMode = Dpi)
└ root : TableLayoutPanel, Dock = Fill, ColumnCount = 1, RowCount = 4
   ├ row 0 : identityStrip   RowStyle Absolute 56
   ├ row 1 : nowBar          RowStyle Absolute 76      (Talk only, Visible = false on Settings)
   ├ row 2 : bodyHost        RowStyle Percent 100
   └ row 3 : statusBar       RowStyle Absolute 30
```

`bodyHost` is a `Panel` with exactly **two** children, both `Dock = Fill`, only
one `Visible` at a time: `talkPage` and `settingsPage`. Switching pages toggles
`Visible` — never `Controls.Clear()`, never `Controls.Add` at switch time, never
`BringToFront` on a control whose parent is the form.

**R2-2 · `talkPage`** — `TableLayoutPanel`, 1 row, 2 columns:
`Percent 100` and **`Absolute 336`**. Padding `20, 18, 20, 18`.
Left cell: another `TableLayoutPanel`, rows `AutoSize` (grid header),
`Percent 100` (card grid, `AutoScroll`), `AutoSize` (known-devices panel),
`AutoSize` (footnote). Right cell: `FlowLayoutPanel`, `FlowDirection.TopDown`,
`WrapContents = false`, children `Width = 336 - padding`.

**R2-3 · `settingsPage`** — `TableLayoutPanel`, 1 row, 2 columns:
**`Absolute 236`** (nav) and `Percent 100` (content host). Content host holds one
`Visible` page panel at a time, same rule as R2-1. Each settings page is its own
`UserControl` with `Dock = Fill`, `AutoScroll = true`, `Padding = 24, 24, 28+SystemInformation.VerticalScrollBarWidth, 24`.

**R2-4 · Kill the ghost strip (05–09, was `S-2`).** The dashed grey line and
clipped glyphs at the top of every settings content area are a control from a
previously shown page still parented to the content host, or a paint of a
disposed control. After R2-3, assert in `Debug` that `contentHost.Controls`
contains exactly the five page `UserControl`s and that exactly one is `Visible`.
The stray white box under the danger block (06) and above the firmware queue
(07) is the same class of bug — a placeholder `Panel` with no children that was
never removed. Delete it.

**R2-5 · Never set `Location` or `Size` on a content control again.** Position
comes from `Dock`, `Margin`, `Padding` and `TableLayoutPanel` cells only. The
only legal absolute numbers are the four in this phase (56 / 76 / 30 / 336 /
236) plus explicit `Width` on inputs, and card height.

**Phase 1 check:** every screen switches with no ghost artifacts, no stray empty
boxes, no control outside its parent's bounds. `Ctrl+F5`, switch every page
twice in both directions, resize to 1024 × 700 and to maximised.

---

## Phase 2 — Widths, so nothing clips its own text

Still no styling work. This phase is only about size.

**R2-6 · Settings nav = 236 px** (unchanged from `S-3`, still ~135). Each item
is a `Label`, `AutoSize = false`, `Dock = Top`, `Height = 40`,
`Padding = 11, 0, 18, 0`, `TextAlign = MiddleLeft`, `AutoEllipsis = false`.
All five labels must render in full: `Set up a device (USB)`, `Group and device
IDs`, `Firmware`, `Identity, audio, shortcuts`, `Diagnostics`.

**R2-7 · Right column = 336 px** (unchanged from `T-27`, still ~200).
`Hold to broadcast` currently renders `Hold to`. Both broadcast buttons are
`Dock = Fill` inside a full-width cell — no `Width`, no `AutoSize`. Verify the
two hint rows render `Everyone in group MESH` / `Space, or Ctrl+Alt+B anywhere`
and `Last sender: none` / `Ctrl+Alt+R` on **one line each**; today "Last sender:
none" wraps to two lines because the panel is too narrow.

**R2-8 · Any `Label` that shows a fixed string gets `AutoSize = true`.** The
`Ac tiv` title in the Activity panel is a label whose `Width` was set to ~24 px.
There is no fixed string in this app that may wrap. The only two strings allowed
to ellipsise remain the card alias and the audio device names.

**R2-9 · Card grid fills its column.** Two cards currently occupy ~590 px of an
~890 px column. Use a `TableLayoutPanel` with `ColumnCount = 3`, three
`Percent 33.33` columns, `Dock = Fill`, `AutoScroll`; cards `Dock = Fill`,
`Margin = 5`, `Height = 120`. With two devices you get two ~293 px cards in
columns 1–2 and an empty column 3 — not two 275 px cards floating left.

**R2-10 · Content max-widths** (unchanged from review 1): USB form 720 px,
firmware page 820 px, diagnostics 860 px. Implement as a `MaximumSize` on the
page's inner stack, left-aligned — not as right padding.

**Phase 2 check:** at 1024 × 700, 1280 × 820 and maximised, no string in the
window is clipped, truncated or wrapped except the two allowed ellipsised ones.

---

## Phase 3 — Rebuild the three empty composites from scratch

These three regions are the worst defects in the build and they share a cause.
Delete the current code for each and write it as below. Each is a `UserControl`
with `AutoSize = true`, `AutoSizeMode = GrowAndShrink`, `Dock = Top`, built from
a `TableLayoutPanel` — never a `Panel` with placed children.

### 3a · Danger block, *Group and device IDs* (screenshot 06)

Right now this is a red-bordered rectangle ~340 px tall containing **nothing**:
the four consequences, the new-group field, the confirmation field, the two
apply-to checkboxes and the `Change group ID` button are all missing. Rebuild:

```
DangerBlock : UserControl  (BackColor #FDF3F3, AutoSize)
└ table : TableLayoutPanel, 1 col, 2 rows, Dock = Fill, AutoSize
   ├ header : padding 14,14,18,14
   │    Title  "Change the group ID"                  11.25 pt Bold  #8E1A1A
   │    Sub    "This breaks the intercom until every node carries the new ID."
   │                                                   9 pt          #7A2020
   └ body : padding 16,16,18,16   (1 px #F2D6D6 rule above, drawn in OnPaint)
        consequences : 4 Labels, 9.75 pt #3D4147, LineHeight ~1.8 (Margin 0,0,0,6)
        newGroup     : label 170 px + TextBox 140 px (letter-spaced caps, MaxLength 4)
        confirm      : label 170 px + TextBox 140 px + hint label
        applyTo      : two CheckBoxes, 8 px apart
        button       : "Change group ID"  + trailing hint label
```

Border: paint it yourself — `OnPaint` draws a 1 px `#B22222` rectangle and fills
a 4 px `#B22222` bar down the left edge. Do not use a 2 px uniform border and do
not use `BorderStyle`.

**Behaviour is an acceptance criterion (spec 4.6/26), and is still wrong.**
`Change group ID` must be `Enabled = false` until the confirmation text box
equals the current group ID exactly (case-sensitive, ordinal). Wire
`TextChanged` on the confirmation box. Disabled paint: `#D08A8A` background,
white text, trailing hint *"Enabled once the confirmation matches."*; enabled:
`#B22222`. Also enforce spec 4.6/26 on the new-group field: exactly 4 chars of
`A–Z0–9`, uppercased on input, anything else rejected in `KeyPress`.

### 3b · Firmware package summary (screenshot 07)

Currently a 25 × 65 px empty white box floating above the queue panel. It should
be the widest panel on the page. Rebuild:

```
PackagePanel : UserControl (white, 1 px #DCDFE3, padding 18,18,20,18, AutoSize)
└ table : 2 cols (Percent 100 | AutoSize), 2 rows
   ├ (0,0) "wifi_intercom 0.7.9"                       11.25 pt Bold #17191C
   ├ (0,1) "wifi_intercom-0.7.9.ota.json · 812.4 KiB · signature verified"
   │                                                    9 pt #63676D, Margin 0,3,0,0
   └ (1,0) rowspan 2, Anchor Right, "Choose another package…"  flat button
```

When no package is loaded, show the same panel with
`No package selected` / `Choose a signed .ota.json package to enable the queue.`
and keep `Start queue…` disabled. Never render the panel empty.

### 3c · Activity panel, Talk right column (screenshots 01, 02)

Currently: a box with `Ac tiv` wrapped over two lines, `Open recordings folder`
overlapping it, a stray 1 px rule floating in white space, and `Diagnostics…`
in a separate detached box. Rebuild as one panel:

```
ActivityPanel : UserControl (white, 1 px #DCDFE3, Width = 336 - padding)
├ header : TableLayoutPanel, 2 cols, padding 11,11,14,11, 1 px #EDEFF1 bottom rule
│    "Activity" 9.75 pt Bold        |  "Open recordings folder" link, Anchor Right
├ rows : Panel, AutoScroll, MaximumSize height = 6 rows
│    per row: time 46 px tabular #83878D | 8×8 px state square | text #3D4147
│    padding 9,0,16,0, 1 px #F2F4F5 rule between rows
└ footer : padding 10,10,16,10 — "Diagnostics…" link, single item
```

`Diagnostics…` is inside this panel's footer, not a separate box below it.

**Phase 3 check:** each of the three regions renders its full designed content
with no empty space, no overlapping children, and no stray rules or boxes.
`Change group ID` starts disabled and enables only on an exact typed match.

---

## Phase 4 — Control substitution (stop using the stock control)

Do this as one sweep across the app. Central helper class, e.g.
`UI.Flat(button)`, `UI.Input(textBox)`, `UI.Combo(comboBox)` — call it from
every construction site so a control cannot be added unstyled.

| Where it still appears | Replace with |
| --- | --- |
| Configure dialog `Speaker volume`, `Ring brightness`, `Device ID` — `NumericUpDown` spinners (04) | `Speaker volume` and `Ring brightness` are the **designed slider** (3 px `#E2E5E8` track, accent fill, 9 × 13 px white handle with 1 px `#7A7F85` border) plus a tabular value cell. `Device ID` is a plain flat `TextBox`, digits only, validated non-zero. No spin buttons anywhere in this app. |
| Sunken `Fixed3D` text boxes — Configure dialog, USB form (04, 05) | `BorderStyle = FixedSingle`, 1 px `#7A7F85`, 26 px tall via font + wrapper padding |
| Chip counts drawn inside a bordered box (01, 02) | The count is **text**, 9 pt `#83878D`, 5 px after the label, inside the chip — not a control of its own. Chip = one custom-painted `Panel`, active state inverted `#17191C` / white. |
| `Wi-Fi network` plain text box (05, was `U-6`) | 240 px `ComboBox`, `DropDownList`, `FlatStyle.Flat`, populated with scanned SSIDs, hint `2.4 GHz only` to its right |
| Log scrollbar drawn inside the padding (09) | `RichTextBox` with `BorderStyle = None` inside a 1 px `#101215` wrapper panel with the padding; the scrollbar belongs to the inner control, outside the visual padding. Fixed height 300 px, and the last visible line must not be cut mid-glyph — set height to a whole multiple of the line height. |
| Stat card bottom edges cut (09) | Cards `AutoSize` from content (padding 14/16, label 8.25 pt caps, value 16.5 pt Bold 6 px under), then the log starts 16 px below the row. Currently the row is clipped by the log's placement. |
| Card overflow `ContextMenuStrip` (03) | Set `RenderMode = Professional` with a flat `ToolStripProfessionalRenderer` colour table: white background, 1 px `#DCDFE3` border, no gradient, no margin gutter, item hover `#F2F4F5`. Items stay `Configure…`, `Get configuration`, `Update firmware…`, `Remove` — correct today. |
| `Settings…` default-button outline (01, 02) | `Form.AcceptButton = null` on the Talk page |

**Phase 4 check:** search the solution for `NumericUpDown`, `TrackBar`,
`ProgressBar`, `Fixed3D`, `SystemColors.` and `UseVisualStyleBackColor = true`
in the content area. Every hit is a defect.

---

## Phase 5 — Remaining per-screen defects

Only after phases 1–4. These are individual and can go in one commit each.

### Talk (01, 02)

- **R2-11** Now-bar accent bar is still flush to the window edge and full
  height. It is **4 × 44 px**, vertically centred, 20 px from the left edge,
  16 px before the text block (was `T-8`).
- **R2-12** The right-aligned now-bar detail line is still missing entirely
  (was `T-9`, `R-1`). Idle: `nothing on the floor · output buffer <n> ms`.
  Receiving: `<alias> · <duration> s · output buffer <n> ms`. Right-aligned,
  9 pt `#63676D`, 20 px from the right edge.
- **R2-13** Identity-strip state dot is a square. It is a **9 px circle**,
  anti-aliased, colour by state (green idle / blue receiving / purple talking /
  amber degraded), 20 px from the left edge (was `T-1`).
- **R2-14** The 1 px × 28 px `#E6E8EA` dividers between identity / mic / speaker
  blocks are still missing (was `T-3`).
- **R2-15** The speaking card (02) is still outlined by a **static** 2 px blue
  rectangle. Required: the card's own 1 px border animates `#DCDFE3` →
  `#1565C0` and back over 2.2 s with a soft blue halo, one 60 ms timer on the
  grid invalidating only that card, timer stopped when nothing speaks (was
  `T-21`). A static outline is not acceptable — the animation is spec §3.3.
- **R2-16** Speaking card's left accent must switch to `#1565C0` while it holds
  the floor; it is still green in 02 (was `R-3`).
- **R2-17** The grid footnote sits ~350 px below the last card, at the bottom of
  the window. It belongs 16 px under the grid content, max width 900 px. Same
  root cause as R2-5.
- **R2-18** *Known, not responding* does not appear at all in these shots.
  Confirm it renders inside the left column (not full-bleed, not below the
  client area) as soon as a device is known and offline (was `T-23`, `T-24`).
- **R2-19** Search box still has a 3-D border and no placeholder styling;
  26 × 150 px, `FixedSingle`, `PlaceholderText = "Alias or ID"` in `#9AA0A6`.

### Configure dialog (04)

- **R2-20** Field widths are inconsistent: `Alias` stretches to the dialog edge
  while every other control is ~100 px. One label column of 150 px, one control
  column, inputs 240 px (alias), sliders full width, `Ring centre` combo 140 px.
- **R2-21** The **Playback** row is missing (spec §3.7): plays / soft muted,
  with the reported hardware slider state shown as read-only text when
  `hw_muted` is set. `Soft mute playback` as a bare checkbox does not carry the
  hardware state.
- **R2-22** No `Cancel`. Row is `Apply to device` (primary blue) + `Cancel`
  (flat), 8 px apart, below a 1 px `#EDEFF1` separator, right-aligned.
- **R2-23** Mark the restart-causing fields inline (a `restarts the device`
  note, 8.25 pt `#83878D`, after brightness / buttons / ring centre / device
  ID), not only in the intro paragraph.
- **R2-24** Dialog is missing its title-row treatment: 16.5 pt Bold title,
  `FormBorderStyle.FixedDialog`, no minimise/maximise, `ShowInTaskbar = false`.

### Set up a device over USB (05)

- **R2-25** `USB port` combo shows only `COM3`; item text is
  `COM3 — Intercom (90b77b2c)` once identified (was `U-10`).
- **R2-26** The identify status text is green before a device is found. It reads
  `Connect a device to identify it over USB` in `#63676D` when idle,
  `Device identified over USB` in `#2E7D32` on success, red + reason on failure.
- **R2-27** The form panel has a second border line ~30 px inside its right edge
  — a nested panel that should not exist. One panel, one border.

### Firmware (07)

- **R2-28** After 3b, the empty-state row keeps its copy in a 14/20 row and the
  queue panel hugs its content instead of running to the bottom of the window.
- **R2-29** Queue rows (not visible in this shot) must match `F-4`: alias 150 px
  Semibold, version transition 170 px, state in state colour, right-aligned row
  action, 6 px flat progress `Panel` 10 px below, detail line 8.25 pt `#83878D`.
- **R2-30** `Start queue…` must open the confirmation dialog with the
  acknowledgement checkbox gating `Start update` (spec 4.7/31), and local PTT +
  Silence must be disabled for the duration (4.7/33).

### Identity, audio and shortcuts (08)

Closest page in the build. Remaining:

- **R2-31** Inputs still use 3-D borders (phase 4).
- **R2-32** The panel runs ~40 px past its last row; `AutoSize` it (was `S-8`).
- **R2-33** `Back to Talk` is wider than its text needs; `AutoSize` +
  padding 10/18, same height as `Save`.

### Diagnostics (09)

- **R2-34** Stat cards are clipped at the bottom (phase 4).
- **R2-35** Log lines are still full ISO timestamps in the source data path;
  the view must show `hh:mm:ss  EVENT  id  detail…` with two spaces after the
  timestamp, and *Copy log* / *Save log to file…* keep the full timestamp
  (was `D-4`). The visible lines look right; verify the copy path differs.

---

## Phase 6 — Copy and colour audit

Re-read every string against `Companion Redesign.dc.html`. The paraphrases
called out in review 1 §G-6 are fixed on the group page; check the remainder,
in particular:

- Now-bar kicker uses the state word set (`IDLE`, `CLAIMING`, `TALKING`,
  `RECEIVING`, `FLOOR OCCUPIED`, `AUDIO UNAVAILABLE`, `NETWORK UNAVAILABLE`).
- Known-devices rows use humanised last-seen (`not heard for 40 minutes`),
  never a `TimeSpan.ToString()`.
- Counts are spelled or parenthesised, never `device(s)`.

Palette: only the values in review 1 §G-5. Type: only the scale in §G-4, from a
single static `Fonts` class. No `SystemColors` in the content area.

---

## Definition of done

1. At 1024 × 700, 1280 × 820 and maximised: no clipped string, no overlapping
   controls, no control outside its parent, no empty or stub panel, no stray
   rule or box.
2. Switching between all five settings pages and back to Talk, twice, leaves no
   ghost artifact anywhere.
3. Every panel in phase 3 renders its full designed content.
4. `Change group ID` is disabled until the typed confirmation matches
   (spec 4.6/26); `Start update` is disabled until the acknowledgement is
   ticked (4.7/31).
5. The speaking card animates; the animation stops within one frame of `END`.
6. No `NumericUpDown`, `TrackBar`, `ProgressBar`, `Fixed3D` border,
   `SystemColors` or default-button focus ring in the content area.
7. Screenshots of each screen at 100 %, 125 % and 150 % DPI overlay the matching
   `design-exports/*.png` with panel edges within a few pixels.

Reference issue IDs (`R2-n`, and the review-1 IDs where quoted) in commit
messages so the next review can be diffed.
