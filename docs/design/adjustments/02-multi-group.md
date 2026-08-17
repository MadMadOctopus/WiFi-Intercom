# Adjustment 2 — multiple groups, other groups on the network, per-device group moves

Applies to `Core`, `Views/TalkView.cs`, `Views/DeviceGridView.cs`,
`Views/Settings/GroupIdsPage.cs`, `Dialogs/ConfigureDeviceDialog.cs`
(phases 3, 4 and 5 of `companion-redesign-handover.md`). The prototype
`prototype/Companion Redesign.dc.html` is updated and is the target.

Read this before building the Talk grid or the group settings page — it changes
the data model those two are built on, so building them the old way first means
building them twice.

## The problem

A device that is announcing normally but carries a different `mesh_id` is
currently classified as *Known, not responding*. That is wrong and it hides a
fault the user needs to see: the device is alive, on the network, healthy, and
simply out of reach because it is in another group. The user has no way to tell
that apart from a dead device, and no way to act on it.

Three changes follow from that, and they are one piece of work:

1. Show the groups we can see but are not in.
2. Let the companion join more than one group.
3. Let a device be moved between groups **individually**, not only as part of a
   change-everything operation.

## The model

- A **device** belongs to exactly one group. Firmware is unchanged: `mesh_id`
  stays a single 4-character field.
- A **companion** may join any number of groups. It receives from all of them.
  It broadcasts to exactly one at a time — the **broadcast target**.
- A **group name** is a companion-local label, stored on this PC, keyed by
  group code. Devices carry only the code. Display is `Workshop (MESH)`; where
  no name is set, the code alone: `YARD`. Never show a name without its code —
  another companion may have named the same group differently, and that is a
  supported situation, so the code is the only shared identity.

Every announcing node therefore falls into exactly one of three buckets:

| Bucket | Condition | Where it appears |
| --- | --- | --- |
| In one of my groups | announcing, `mesh_id` ∈ joined | device grid, under its group heading |
| In another group | announcing, `mesh_id` ∉ joined | **Other groups on this network** |
| Silent | not announcing on any group | **Known, not responding** |

`Known, not responding` must never contain a device that is announcing on some
other group. Its subtitle is now "…kept from earlier sessions, **silent on every
group** — they return to the grid as soon as they announce".

## Core changes

- `CompanionSettings`: `JoinedGroups` — an ordered list of
  `{ Code, Name }`, plus `BroadcastTargetCode`. Migration: an existing
  single-group setting becomes a one-entry list, target = that entry. A
  companion with an empty list is not a valid state; keep at least one.
- Discovery already receives every announcement on the multicast address.
  **Stop filtering by group at the socket.** Filter at the model: a `Peer`
  carries its `GroupCode`, and the peer store partitions by it.
- Audio receive accepts a stream whose group is any joined group; it rejects
  others as it does today. Floor control is **per group** — two groups on one
  LAN never mix, and holding the floor in `MESH` must not block a sender in
  `BNCH`. This means the receive session keys its floor state by group code, not
  globally. Check this carefully; it is the one place where multi-group can
  break existing behaviour.
- Transmit sends to the broadcast target's group only. `Hold to reply` sends to
  the **last sender's group**, which may not be the target group; if the last
  sender's group is no longer joined, the reply button is disabled with
  `Last sender: <alias> · <group> — not joined`.
- Leaving a group: peers in it move out of the grid immediately. If the group
  was the broadcast target, the target moves to the first remaining joined group
  and the now bar reports the change.

## Talk screen

**Identity strip.** Second line becomes
`ID a9d5b153 · 2 groups joined · broadcasting to Workshop (MESH) ▾`.
The trailing part is a link-styled label with a `▾`; clicking it drops a menu
of the joined groups (radio-marked, current one checked) plus a separator and
`Group settings…`. Picking a group changes the broadcast target immediately —
no dialog. With exactly one group joined, drop the count and render
`ID a9d5b153 · group Workshop (MESH)`, no chevron, no menu. Do not make the
single-group case feel like a multi-group app.

**Grid header count.** `16 devices across 2 joined groups`. With one group,
keep today's `16 of 16 in group Workshop (MESH)`.

**Grid sections.** When more than one group is joined, the card grid gains a
heading row per group, in joined order, and each section is its own 3-column
`TableLayoutPanel`:

```
sectionHeader : TableLayoutPanel, Dock = Top, AutoSize, Margin 0,18,0,8 (0 top for the first)
   mark   3 × 15 px  #1565C0 if target else #C9CCD0
   title  "Workshop (MESH)"      9.75 pt Bold
   count  "13 of 16 devices"     9 pt #63676D
   right  target chip  — "BROADCAST TARGET", 8.25 pt Bold, white on #17191C, 3/8 padding
          non-target  — "Broadcast here", 8.25 pt #1565C0, transparent, click sets the target
```

With one group joined, no heading is drawn at all. Filter chips and the search
box filter across all sections and the counts stay global.

**New panel: Other groups on this network.** Sits between the card grid and
`Known, not responding`, same panel chrome (white, 1 px `#DCDFE3`, header with a
1 px `#EDEFF1` bottom rule). Header: **Other groups on this network** +
"Announcing normally, but this companion is not a member — join a group to hear
it and talk to it". One row per group:

```
dot 6 × 6 #9AA0A6 | title 150 px Semibold | count 110 px #63676D
| members  (alias list, ellipsised)
|   note   8.25 pt second line
| "Join group…" flat button, Anchor Right
```

The note line carries the two cases worth calling out: `no name set on this PC`
in `#83878D`, and — when the group contains a device from the known-devices
store — `you know this device — it left Workshop on 12 Aug` in `#7A5200`. That
second line is the whole point of the panel: it is how a user discovers that a
device they think is broken has simply been moved.

A device appears in exactly one place at a time. A row in this panel must never
name a device that is also drawn as a card in the grid; if you see that, the
partition by group code is wrong.

Hide the panel entirely when no foreign group is announcing. Never show it
empty.

`Join group…` joins that code straight away, with no name, and shows a toast-free
confirmation by the devices simply appearing in the grid; the name can be set
later in Settings. Do not open a dialog for a one-field action whose value is
already known.

## Settings · Group and device IDs

The page is restructured. Intro copy is updated in the prototype; use it
verbatim.

**1. `Groups this companion has joined`** (replaces the old *Current group*
panel). Header + "The selected group is where Hold to broadcast sends". One row
per joined group:

```
RadioButton (broadcast target) | title 200 px Semibold | "13 of 16 devices" 150 px
| note  "broadcast target" / "receiving only"  #83878D
| Rename…   Leave     flat buttons, Anchor Right
```

Footer row, `#F7F8F9`: `Join another group` + a 96 px `CODE` box + a 210 px
`Name on this PC (optional)` box + `Join`, then a live hint naming the codes
currently announcing (`YARD and LOAD are announcing nearby`). `Join` is disabled
until the code is 4 × `A–Z0–9` and not already joined.

`Leave` on the last remaining group is disabled — a companion always belongs to
at least one group. `Leave` on the broadcast target moves the target to the next
row first.

**2. Identity panel** — companion ID, plus a `Group names` line explaining that
names live on this PC only.

**3. Danger block — now `Move devices to another group`.** This is the
per-device change, and it replaces the old all-at-once *Change the group ID*.
Same red chrome (1 px `#B22222`, 4 px left bar, `#FDF3F3`). Header:
"Move devices to another group" / "Pick the devices to move. Each one reboots
and leaves the group it is in now." Four consequences, then:

```
Devices to move   : a scrolling checked list, 1 px #DCDFE3, MaximumSize height
                    = 5 × row height, so the list never clips a row mid-line
     per row: fixed 33 px, no wrapping
              CheckBox | alias 118 px Semibold, ellipsised | id 96 px
              | "Workshop (MESH)" ellipsised
              | reachability, right-aligned:
                  "reachable"        #2E7D32
                  "legacy, USB only" #7A5200
                  "unreachable"      #83878D   (checkbox disabled)
Destination group : 220 px DropDownList of joined groups + "or type a new code"
Type BNCH to confirm : 140 px text box
[ Move 2 devices to BNCH ]   + "Enabled once the confirmation matches."
```

Rules:

- The list contains every device the companion knows, from all joined groups and
  from the known-devices store; unreachable ones are listed but not selectable,
  so the user can see what will **not** be moved.
- The button label counts the ticked devices and names the destination, and
  updates as either changes. Zero ticked → disabled, label
  `Move devices to BNCH`.
- The confirmation gate from spec 4.6/26 still applies, now against the
  **destination** code: `Enabled = false` until the typed text equals it exactly
  (ordinal), and at least one device is ticked.
- Devices are written **one at a time**, in list order, each with its own
  result. A failure stops nothing else: the row shows the failure and the run
  continues. Report at the end as `4 moved, 1 failed — Reception did not
  acknowledge`.
- If the destination group is not joined, ask once, inline, before starting:
  `You are not in BNCH. Join it as well, so the moved devices stay visible?`
  with `Join and move` / `Move anyway`. Default is `Join and move`.

## Configure dialog

A `Group` row directly under `Alias`: a 220 px `DropDownList` of joined groups
plus the device's current group if it is not one of them, and the note
`moving restarts the device` in 8.25 pt `#83878D`. Applying a changed group here
moves that one device — same write path as the danger block, single device, and
it still needs no typed confirmation because it is one explicit device the user
opened by name.

Add `Move to another group…` to the card overflow menu, between
`Get configuration` and `Update firmware…`; it opens the Configure dialog with
the group combo focused.

## Copy

- Never `group MESH` alone in prose where a name may exist — use the
  `Name (CODE)` form.
- The now bar detail line leads with the sender's group when more than one is
  joined: `Workshop (MESH) · broadcast to the group · 3.2 s · output buffer 96 ms`.
- Activity rows name the group when it is not the broadcast target:
  `Bench Intercom is speaking · Bench room (BNCH)`.

## Check

- A device moved to a group you are not in leaves the grid and appears in
  *Other groups on this network* within one announce interval, never in
  *Known, not responding*.
- Join that group: it moves into a new grid section; leave again: it goes back.
- Two devices speaking in two different joined groups at once do not block each
  other, and the now bar reports the one whose audio is actually playing.
- With one group joined, the Talk screen is visually identical to the
  single-group design: no headings, no chevron, no counts about groups.
- Move one device from the Configure dialog and one batch of three from the
  danger block; both write one device at a time and both survive one device
  failing.
