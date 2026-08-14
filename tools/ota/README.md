# OTA release packaging

The private release key is never committed. The development key used for local
hardware testing lives in the ignored `.ota-keys/` folder; replace the public
key in `firmware/main/ota_public_key.h` and `companion/.../OtaPackage.cs`
together before production manufacturing.

Build an ESP-IDF image, then create its signed manifest on Windows:

```powershell
.\tools\ota\Create-OtaPackage.ps1 `
  -Image .\firmware\build\wifi_intercom.bin `
  -Version 0.7.1 `
  -PrivateKey .\.ota-keys\dev-release-key.pem
```

Choose the resulting `wifi_intercom.bin.ota.json` in the companion. It verifies
the image hash and signature before opening a temporary, tokenised local HTTP
endpoint. A device verifies the same signature and streamed hash before it
ever marks the inactive OTA partition bootable.
