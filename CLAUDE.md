# CLAUDE.md — WiFi-Intercom

Working branch: `codex/companion-p2-redesign`. Base: `main`.

This file is the standing brief for anyone (human or agent) working in this
repo. Read it before the first edit of a session.

## What this repo is

- `firmware/main/` — ESP-IDF firmware for the XIAO ESP32-S3 intercom node.
- `companion/IntercomCompanion/` — .NET 10 WinForms companion app for Windows.
- `tools/` — helper scripts, not part of the shipped product.
- `docs/` — protocol, architecture and design documents.

The current effort is a **companion UI redesign plus a p1 → p2 protocol
uplift**. Two agents have attempted the UI and both produced a window that does
not match the design. Read `docs/design/companion-redesign-handover.md` before
touching `companion/` — it explains why, and what to do instead.

## Source of truth, in order

1. `docs/design/companion-redesign-spec.md` — behaviour, protocol, acceptance
   criteria (numbered 1–39). These are testable and binding.
2. `docs/design/prototype/Companion Redesign.dc.html` — the visual target. Open
   it in a browser; the tab strip switches between the nine screens. Everything
   in it is a spec unless the brief says otherwise.
3. `docs/design/prototype/design-exports/*.png` — one PNG per screen, for
   pixel comparison.
4. `docs/design/companion-ui-review-2.md` — the defect list from the last build,
   with issue IDs (`R2-n`).

Sample devices in the prototype (`Intercom`, `Workshop`, `Old bench board`) are
illustrative data. Do not hard-code them.

## Companion UI rules (non-negotiable)

These exist because every previous failure traces to breaking one of them.

1. **No absolute positioning of content.** No `Control.Location`, no
   `Control.Size` on a content control. Position comes from `Dock`, `Margin`,
   `Padding` and `TableLayoutPanel` cells. The only legal fixed numbers are
   documented band heights (56 / 76 / 30), the two column widths (336 / 236),
   card height and explicit input widths.
2. **No `Controls.Clear()` on a live container, ever.** Page switching toggles
   `Visible` on pre-built pages. Data refresh updates existing controls in place
   or rebuilds a single leaf container that owns nothing else.
3. **No re-parenting of controls between layouts.** A control belongs to one
   parent for its lifetime.
4. **Nothing in `Settings` may block discovery, the device grid or PTT.** The UI
   thread owns controls; network and audio stay on background tasks. See
   `docs/windows-companion-design.md`, still binding.
5. **No stock control chrome in the content area.** No `NumericUpDown`, no
   `TrackBar`, no `ProgressBar`, no `BorderStyle.Fixed3D`, no `SystemColors.*`,
   no `UseVisualStyleBackColor = true`. Use `UiStyles` and `FlatSlider`.
6. **Every label carrying a fixed string is `AutoSize = true`.** Only the device
   alias and audio device names may ellipsise. Nothing else may clip or wrap.
7. **Colour and type come from `UiStyles` only.** No new `Color.FromArgb` and no
   new `new Font(...)` outside that class.
8. **Strings are verbatim from the prototype.** Do not paraphrase UI copy.

## Verification before you say a UI task is done

Build and run — screenshots of the reasoning, not the intention.

```
dotnet build companion/IntercomCompanion
dotnet run  --project companion/IntercomCompanion
```

Then, every time:

- Resize to 1024 × 700, to 1280 × 820, and maximised.
- Switch every settings page and back to Talk, twice, in both directions.
- Confirm: no clipped string, no overlapping controls, no control outside its
  parent, no empty or stub panel, no leftover rule or box.
- Compare against the matching `design-exports/*.png`.

Do not report a phase complete without having run the app.

## Commit conventions

- One phase per commit (phases are defined in the handover document).
- Reference issue IDs (`R2-14`) and acceptance criteria (`spec 4.6/26`) in the
  message body.
- Do not mix a refactor and a visual change in one commit.

## Firmware

The p2 protocol changes (`IH3` HELLO, `soft_mute`, `hw_muted`, `mesh_id`,
`device_id` in NVS) are specified in `docs/design/companion-redesign-spec.md`
§2. A p2 node must stay interoperable with p1 nodes for audio and discovery.
Do not change the codec, frame size or ports.

## Machine-local instructions

IMPORTANT: Also read `CLAUDE.local.md` (machine-local, not committed) - it contains private values (account name, profile URL, local-only notes) that must NEVER be committed or pushed.
