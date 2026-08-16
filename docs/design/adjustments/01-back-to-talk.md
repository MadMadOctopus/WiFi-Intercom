# Adjustment 1 — returning to Talk from Settings

Applies to `Views/SettingsView.cs` and `Views/Settings/IdentityPage.cs`
(phases 2 and 4 of `companion-redesign-handover.md`). The prototype
(`prototype/Companion Redesign.dc.html`) has been updated to match — treat it as
the target.

## The problem

`Back to Talk` existed only as a button at the bottom of the *Identity, audio,
shortcuts* page, below `Save`. From the other four settings pages there was no
way back except the window's own chrome. It also read as part of the save row,
so it looked like "discard and leave" rather than plain navigation.

## The fix

Navigation belongs in the navigation column, not in one page's form. Move it to
a permanent first row in the settings nav, above the `SETTINGS` section label,
and delete the per-page button.

### 1. Nav back row — `SettingsView`

The nav column (`Absolute 236`, `#EEF0F2`, 1 px `#DCDFE3` right border) gains a
`Dock = Top` row **before** the section label. Column padding becomes
`0, 12, 0, 12`.

```
backRow : Panel, Dock = Top, Height = 38, Cursor = Hands
   glyph  "←"        10.5 pt        #1565C0   left inset 15
   text   "Back to Talk"  9.75 pt   #1565C0   6 px after the glyph
   hint   "Esc"      8.25 pt        #83878D   Anchor Right, right inset 18
separator : Panel, Dock = Top, Height = 1, BackColor #DCDFE3, Margin 0,10,0,12
sectionLabel : "SETTINGS"  (unchanged)
```

Hover: whole row `#E4E7EA` — set on `MouseEnter` / `MouseLeave` on the panel,
and pass the same handlers to the child labels so the highlight does not drop
out when the pointer crosses a label. The glyph and text are two labels in a
`TableLayoutPanel`, not one string with a padded arrow, so the arrow stays
optically aligned.

The row is a nav affordance, not a nav item: no left `3 px` accent bar, no
active state, never highlighted as selected.

Click calls the same page switch the identity strip's `Settings…` link uses in
reverse — `ShowTalk()` on the shell, which sets `TalkView.Visible = true`,
`SettingsView.Visible = false`, `NowBar.Visible = true`. Nothing is rebuilt or
disposed (`CLAUDE.md` rules 1–3).

### 2. Esc closes settings

`MainForm` already has `KeyPreview = true`. In `ProcessCmdKey`, when
`SettingsView.Visible` and the key is `Keys.Escape`, call `ShowTalk()` and
return `true`. Two conditions where Esc must **not** navigate: a modal dialog is
open (it is not, if it is modal — `ProcessCmdKey` on the form is not reached),
and focus is in a text box with an open `ComboBox` drop-down. Guard the second
with `ActiveControl is ComboBox { DroppedDown: true }`.

Do not add a mnemonic or a second shortcut. Esc plus the nav row is the whole
affordance.

### 3. Remove the per-page button

In `IdentityPage`, the action row is now `Save` alone — same primary style, same
position. Delete the `Back to Talk` button and its handler. Do not replace it
with `Cancel`: this page saves in place, there is nothing to cancel.

Check the other four pages for a copy of the same button; the last build had it
only on Identity, but the review-1 defect list mentioned it appearing twice.

### 4. Unsaved-changes case

If a settings page has edits pending when the user leaves — Identity is the only
one that batches into a `Save` — leaving must not silently discard them. Simplest
correct behaviour, and what the prototype implies: **Identity fields commit on
`Save` only, and leaving with pending edits keeps them in the controls.** The
page is not rebuilt, so returning to it shows the same unsaved values. Do not
add a confirmation dialog for this.

## Check

- From all five settings pages, the nav row and Esc both return to Talk, and the
  now bar reappears.
- The nav row never shows a selected state, including right after a click.
- Hover highlight covers the full 236 px width and does not flicker when moving
  across the glyph, text and `Esc` hint.
- `Identity` shows one button, `Save`, and nothing shifted position.
- Esc inside the alias text box returns to Talk; Esc with a combo drop-down open
  closes the drop-down and stays in Settings.
