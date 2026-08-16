# Companion logo & app icon — integration guide for Codex

Target repo: `MadMadOctopus/WiFi-Intercom`, branch `main`, project `companion/IntercomCompanion`
(a .NET WinForms application). Every path below is relative to the repository root unless it
starts with `companion/`.

This guide is written to be followed literally. Do not redraw, re-crop, recolour or regenerate any
of the artwork. The files are final. Your job is to copy them into the repo, register them as
resources, and wire them into the four places the app shows an icon.

---

## 1. What the mark is

Top-down view of the device: knit mesh shell ring, white top plate, and the two buttons. The two
buttons together form one circle split by a vertical seam — the emerald button on the right is the
talk button, the blue button on the left is 80 % of its size. Do not swap the colours or sides.

Palette (use these exact values anywhere the UI needs to match the brand):

| Token | Hex | Used for |
| --- | --- | --- |
| Emerald | `#12A06B` (gradient `#3FD39A → #0A7A51`) | large talk button |
| Blue | `#1E6FD0` (gradient `#5AA9F0 → #123F86`) | small button |
| Shell | `#A9C6E2` (gradient `#DCEAF7 → #A9C6E2`) | mesh shell ring |
| Shell outline | `#8FB0CF` light / `#8FC0E8` dark | ring stroke |
| Plate | `#FFFFFF → #E3E8EE` | top plate |
| Ink | `#17191C` | one-colour cut, wordmark |

---

## 2. Asset inventory

All assets are delivered in the `brand/` folder of the design project. Copy the whole folder into
the repo at `companion/IntercomCompanion/Assets/Brand/` keeping the file names byte-identical.

### Vector (source of truth)

| File | Purpose |
| --- | --- |
| `logo-mark.svg` | Full-colour mark, transparent background, 512 × 512 viewBox |
| `app-icon-light.svg` | App icon on a light rounded-square plate |
| `app-icon-dark.svg` | App icon on a dark rounded-square plate |
| `app-icon-small-light.svg` | Simplified icon (no mesh texture) for ≤ 32 px |
| `app-icon-small-dark.svg` | Same, dark |
| `logo-mono-ink.svg` | One-colour cut, ink on white |
| `logo-mono-reverse.svg` | One-colour cut, white on ink |
| `logo-lockup.svg` | Horizontal lockup: mark + "Intercom Companion", 640 × 160 |

### Raster

`brand/png/` contains, for both `light` and `dark`:
`app-icon-{theme}-{16,20,24,32,48,64,128,256,512}.png`
plus `logo-mark-256.png`, `logo-mark-512.png`, `logo-mono-ink-256.png`, `logo-lockup-1280.png`.

The 16/20/24/32 PNGs are rendered from the **simplified** SVG (mesh dropped, plate enlarged) and
the 48 px and up from the full one. This is intentional — do not regenerate the small sizes by
downscaling the 512 px art, it turns to mud in the tray.

### Windows icon containers

| File | Contents |
| --- | --- |
| `app.ico` | 8 frames: 16, 20, 24, 32, 48, 64, 128, 256 — light theme, PNG-compressed, 32-bit with alpha |
| `app-dark.ico` | Same frame list, dark theme |

`app.ico` is the application icon. `app-dark.ico` is only for a future dark title-bar / dark tray
variant; register it but do not use it yet.

---

## 3. Repository changes, step by step

### Step 3.1 — Add the files

```
companion/IntercomCompanion/Assets/Brand/app.ico
companion/IntercomCompanion/Assets/Brand/app-dark.ico
companion/IntercomCompanion/Assets/Brand/logo-mark.svg
companion/IntercomCompanion/Assets/Brand/logo-lockup.svg
companion/IntercomCompanion/Assets/Brand/logo-mono-ink.svg
companion/IntercomCompanion/Assets/Brand/logo-mono-reverse.svg
companion/IntercomCompanion/Assets/Brand/app-icon-light.svg
companion/IntercomCompanion/Assets/Brand/app-icon-dark.svg
companion/IntercomCompanion/Assets/Brand/app-icon-small-light.svg
companion/IntercomCompanion/Assets/Brand/app-icon-small-dark.svg
companion/IntercomCompanion/Assets/Brand/png/… (all PNGs, keep the png/ subfolder)
```

Commit them as binary. Do not run them through any image optimiser — the `.ico` frame table is
hand-built and some optimisers rewrite it into a single frame.

### Step 3.2 — Project file

In `companion/IntercomCompanion/IntercomCompanion.csproj`, inside the first `<PropertyGroup>`:

```xml
<ApplicationIcon>Assets\Brand\app.ico</ApplicationIcon>
```

and add an `<ItemGroup>`:

```xml
<ItemGroup>
  <None Update="Assets\Brand\**\*" CopyToOutputDirectory="PreserveNewest" />
  <EmbeddedResource Include="Assets\Brand\app.ico" LogicalName="IntercomCompanion.app.ico" />
  <EmbeddedResource Include="Assets\Brand\png\logo-lockup-1280.png" LogicalName="IntercomCompanion.logo-lockup.png" />
  <EmbeddedResource Include="Assets\Brand\png\logo-mark-256.png" LogicalName="IntercomCompanion.logo-mark-256.png" />
</ItemGroup>
```

Notes that trip people up:

- `<ApplicationIcon>` uses a **backslash** path relative to the `.csproj`, and the file must exist
  at build time or the build fails with `CVTRES : fatal error CVT1103`.
- If the project already has an `<ApplicationIcon>` line pointing at an old icon, replace it;
  do not add a second one.
- If `EnableDefaultNoneItems` is disabled in this project, the `<None Update=…>` line must become
  `<None Include=…>`. Check before assuming.

### Step 3.3 — A single place that owns the icons

Create `companion/IntercomCompanion/Core/BrandAssets.cs`:

```csharp
using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace IntercomCompanion.Core
{
    /// <summary>Loads the brand icon and logo bitmaps from embedded resources exactly once.</summary>
    internal static class BrandAssets
    {
        private static Icon? _appIcon;
        private static Image? _lockup;
        private static Image? _mark;

        /// <summary>Multi-frame application icon (16 → 256 px).</summary>
        public static Icon AppIcon => _appIcon ??= Load<Icon>("IntercomCompanion.app.ico", s => new Icon(s));

        /// <summary>Horizontal lockup, 1280 × 320, for the About box.</summary>
        public static Image Lockup => _lockup ??= Load<Image>("IntercomCompanion.logo-lockup.png", Image.FromStream);

        /// <summary>Square mark, 256 × 256, transparent background.</summary>
        public static Image Mark => _mark ??= Load<Image>("IntercomCompanion.logo-mark-256.png", Image.FromStream);

        /// <summary>Icon rendered at a specific pixel size, so Windows does not rescale a wrong frame.</summary>
        public static Icon AppIconAt(int px) => new Icon(AppIcon, new Size(px, px));

        private static T Load<T>(string logicalName, Func<Stream, T> factory)
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);
            if (s == null)
                throw new InvalidOperationException($"Embedded brand resource '{logicalName}' is missing. " +
                                                    "Check the <EmbeddedResource LogicalName=…> entries in IntercomCompanion.csproj.");
            return factory(s);
        }
    }
}
```

Do not load these from disk with `new Icon("Assets/Brand/app.ico")`. The working directory differs
between F5, `dotnet run` and a published single-file build, and the disk path will fail in at least
one of them.

### Step 3.4 — Main window

In `companion/IntercomCompanion/MainForm.cs`, in the constructor **after** `InitializeComponent()`:

```csharp
Icon = Core.BrandAssets.AppIcon;
ShowIcon = true;
```

If `MainForm.Designer.cs` or `MainForm.resx` still carries an old `this.Icon = …` assignment or an
embedded `$this.Icon` resource entry, delete it — the designer resource wins over anything set in
the designer file but loses to the constructor line above, and leaving both makes the change look
like it did not apply.

The redesigned window (see `docs/companion-redesign-spec.md`) draws its own 14 px title-bar glyph in
the top-left of the client area. Replace whatever placeholder is there with:

```csharp
// title strip, left edge — 16 px logical, DPI-scaled
titleGlyph.Image = Core.BrandAssets.AppIconAt(ScaleToDpi(16));
titleGlyph.SizeMode = PictureBoxSizeMode.CenterImage;
```

where `ScaleToDpi(int)` is the existing helper if the project has one; otherwise
`(int)Math.Round(16 * DeviceDpi / 96.0)`. Round to the nearest **available frame** — 16, 20, 24, 32,
48 — rather than an arbitrary size: `Icon(Icon, Size)` picks the closest frame and then stretches,
and a 22 px request produces a visibly soft glyph at 150 % scaling.

### Step 3.5 — Tray / notify icon

If `NotifyIcon` is used (it is referenced by the "Mute my speaker" background behaviour):

```csharp
trayIcon.Icon = Core.BrandAssets.AppIconAt(SystemInformation.SmallIconSize.Width);
trayIcon.Text = "Intercom Companion";
```

`SmallIconSize` already accounts for DPI. Assigning `BrandAssets.AppIcon` directly also works but
lets Windows pick the frame; the explicit call is what we want in the tray.

### Step 3.6 — About box

If there is no About dialog yet, do not invent one — skip this step and report it. If there is:

- Replace the existing logo `PictureBox` image with `Core.BrandAssets.Lockup`.
- Set `SizeMode = PictureBoxSizeMode.Zoom` and give the box a 4 : 1 aspect (e.g. 320 × 80 logical).
- Background must stay light (`#FFFFFF` or `#F5F6F7`). The lockup wordmark is ink `#17191C` and is
  not legible on a dark panel — the dark variants are the `-dark` files, and there is no dark
  lockup yet.

### Step 3.7 — Installer / packaging

- If there is a WiX / Inno / MSIX manifest under `companion/`, point its icon reference at the same
  `Assets\Brand\app.ico`. Do not embed a copy of the PNG.
- For MSIX, use `app-icon-light-256.png` for `Square44x44Logo` and `Square150x150Logo` sources and
  let the packaging tool generate scaled assets from the 256 px file.
- Shortcut icons: `app.ico`, index 0.

---

## 4. Acceptance checklist

Verify each item and report the result. Do not mark an item done without running the app.

1. `dotnet build companion/IntercomCompanion` succeeds with no `CVT1103` and no missing-resource warning.
2. The built `.exe` shows the mark in Explorer at Large, Medium and Small icon views, and in the
   Details view — all four must show the mark, not the default WinForms window glyph.
3. Running the app, the taskbar button shows the mark. Alt+Tab shows the mark.
4. The window title bar shows the mark at 100 %, 150 % and 200 % display scaling, sharp at each.
5. At 16 px the two buttons are still distinguishable as separate emerald and blue shapes; the mesh
   texture is absent (expected — the small frames drop it deliberately).
6. If a tray icon exists, it renders sharp at both 100 % and 200 % scaling.
7. No new file is written to disk at runtime, and no `FileNotFoundException` appears in the
   diagnostics log when the app is published as single-file (`dotnet publish -p:PublishSingleFile=true`).

## 5. Explicitly out of scope

- Do not restyle any existing UI colours to match the logo. The window palette stays as specified in
  `docs/companion-redesign-spec.md`.
- Do not add a splash screen.
- Do not generate additional icon sizes, favicons, or platform variants.
- Do not modify the firmware side or any device-facing asset.
