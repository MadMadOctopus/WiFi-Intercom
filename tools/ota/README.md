# OTA release packaging

Use this folder to make a signed application-only OTA package. The private
release key is never committed. The development key used for local hardware
testing lives in the ignored `.ota-keys/` folder.

Before production manufacturing, replace the public key in both
`firmware/main/ota_public_key.h` and
`companion/IntercomCompanion/Core/OtaPackage.cs` together. A package signed by
one key is rejected if either the companion or device trusts a different key.

## Create a package

Build an ESP-IDF image, then create its signed manifest on Windows:

```powershell
.\tools\ota\Create-OtaPackage.ps1 `
  -Image .\firmware\build\wifi_intercom.bin `
  -Version 0.7.8 `
  -PrivateKey .\.ota-keys\dev-release-key.pem
```

The result is a `.ota.json` manifest next to the image. Choose that manifest,
not the `.bin`, in the companion. Keep the manifest and image together when
moving a release.

The packaging script defaults to protocol **3**, matching current firmware and
companion. Older protocol targets must be updated together before deployment.

The package contains the image file name, version, protocol revision, byte
size, SHA-256 hash and ECDSA signature. The companion verifies it before
opening a temporary tokenised local HTTP endpoint. A device verifies the same
signature and streamed hash before it marks the inactive OTA partition
bootable.

## Update workflow

1. Ensure a dual-slot OTA-capable firmware is already installed over USB.
2. Build the intended firmware and create a package with a version newer than
   the running device version.
3. Choose the manifest in the companion and update an active device. Keep the
   companion running until the device has rebooted and re-announced the offered
   version.
4. If Windows asks, allow the companion's temporary HTTP listener through the
   firewall on the private LAN only.

OTA updates only the application partition. It preserves NVS configuration and
does not update the bootloader or partition table. A failed transfer or failed
health check returns the device to its previous application automatically.

`Capture-Com6Console.ps1` and `Start-OtaDiagnostic.ps1` are hardware-test
helpers. They are not required for normal releases.
