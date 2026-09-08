# p3 local validation

Validated on a Linux homelab with no attached ESP32, microphone, speaker or
serial device. The Windows Forms application was cross-built, never launched.

## Commands and results

Run from the repository root unless a working directory is specified.

| Command | Result |
| --- | --- |
| `IDF_PATH=/srv/projects/esp-idf-v5.4.4 bash tests/firmware/run.sh` | Pass: production C PTT1/HELLO codec, broadcast policy, directed priority, JSON validation, NVS v1/v2/v3 migration, persistence, password non-disclosure, shared USB/remote transaction concurrency and USB reply capacity; ASan/UBSan/LeakSanitizer enabled |
| `dotnet run --project tests/Companion.HostTests` | Pass: production companion state machine with simulated network/audio boundaries, wire/version/capability codecs, configuration field roundtrips and assistant selection/resolution |
| `dotnet build companion/IntercomCompanion -p:EnableWindowsTargeting=true` | Pass, zero warnings/errors; .NET 10 Windows Forms cross-build |
| `idf.py -C firmware -B build-diagnostic -DINTERCOM_USB_RAW_MIC_CAPTURE=ON build` (after sourcing IDF) | Pass: optional USB diagnostic firmware also fits its OTA slot |
| `dotnet build tools/MicTransportCapture` | Pass, zero warnings/errors; capture utility updated to p3 discovery |
| `python3 tests/test_ota_package.py` | Pass: actual PowerShell packager, ephemeral test key/image, p3 default, hash/signature verification and tamper rejection |
| `PYTHONPYCACHEPREFIX=/tmp/wifi-intercom-pycache python3 -m py_compile tools/serial_log.py tools/capture_device_audio.py` | Pass, syntax compilation only |
| `git diff --check` | Pass |

Firmware build, from `firmware/`:

```bash
export IDF_TOOLS_PATH=/srv/projects/esp-idf-tools-v5.4.4
source /srv/projects/esp-idf-v5.4.4/export.sh
idf.py build
```

Pass: production ESP32-C3 application and bootloader build, application fits
the OTA slot with approximately 30% free (production image `0x100790` bytes; slot `0x170000`). The clean configuration reported the
pre-existing unknown `ESP_WIFI_POWER_SAVE_NONE` Kconfig setting; runtime already
calls `esp_wifi_set_ps(WIFI_PS_NONE)`. No compiler warning was introduced by this
change. Firmware version reporting remains intact at the repository's 0.7.8.

The preinstalled IDF 6.1 build was attempted first and failed because the project
requires the bundled `json` component from IDF 5.4.4. The documented SDK and its
C3 toolchain were then installed in separate directories and the build passed.
The SDK installation is outside this repository. Initial sandbox-only cache,
network and sanitizer restrictions were resolved by running the affected local
validation tools with the appropriate permissions.

There were no existing automated .NET test projects on main. The new
platform-neutral test executable links the production state machine and codecs;
it substitutes hardware/IO boundaries rather than maintaining another session
implementation. Firmware tests compile production protocol/configuration/USB
code and the pure policy helpers with NVS, GPIO and FreeRTOS host substitutes.
They do not run the complete ESP32 task scheduler or physical drivers.

## Covered scenarios

- Broadcast contention yields one transmitter; the tuple tie-break and 500 ms
  occupied-floor limit remain in place.
- Independent directed sessions coexist, with no reservation on uninvolved
  nodes; a broadcast reaches an available receiver alongside them.
- Directed reception replaces broadcast reception locally without changing
  broadcast ownership. Broadcast RTP and END do not disrupt directed reception.
- Endpoint collisions return BUSY; the rejected caller can select a different
  endpoint. Local TX rejects directed claims. Crossed directed attempts cannot
  establish conflicting transmitters; abandoned reservations expire.
- Lost ACCEPT fails closed; timeout releases its orphan receiver reservation.
- HELLO roundtrips zero, OTA, both assistant bits, combinations and unknown bits.
  C and C# agree on a golden big-endian packet; obsolete control is rejected.
- Assistant disabled/unset defaults survive migration, including poisoned old
  struct padding. Enable/select/change/persist, omitted fields, invalid values,
  failed writes, existing settings and non-disclosure are checked.
- Service options filter by capability/group/revision/liveness, use stable IDs,
  preserve an offline ID and resolve a changed address without substituting a
  different service. Non-client devices cannot configure assistance in the UI
  policy. Actual WinForms layout remains untested.

## GitHub validation

`.github/workflows/validate.yml` runs the companion simulations, both .NET
builds, OTA packager test, firmware sanitizer tests and an ESP-IDF 5.4.4 build.
The PR checks must pass before merge. The PR check runs provide the authoritative
GitHub result for the final commit.

## Not physically tested

- ESP32 flashing, real NVS reboot/power-loss behaviour and USB provisioning.
- Microphone capture, speaker output, mute slider/buttons and ring animations.
- LAN/Wi-Fi packet loss, timing, arbitration and concurrent physical devices.
- Actual signed OTA transfer, flash-slot switching, reboot and rollback.
- Windows audio devices, hotkeys, tray integration and WinForms layout/resizing.

Remaining risks are real radio/audio scheduling and latency under loss, physical
USB/NVS/OTA behaviour, and Windows GUI presentation. The directed handshake is
bounded by the existing 100 ms claim window; delayed/lost ACCEPT can reject an
otherwise free receiver, safely. This revision is intentionally incompatible
with obsolete deployment revisions and requires updating all deployment nodes.
