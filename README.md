# Wi‑Fi Intercom

Production firmware and desktop companion for USB-powered Seeed XIAO ESP32-C3 voice intercoms. Devices use UDP multicast discovery on one Wi‑Fi LAN; there is no server and no configured peer-IP list.

## Production hardware

| Function | XIAO pin |
| --- | --- |
| MAX9814 microphone OUT | D1 / GPIO3 / ADC1_CH3 |
| MAX98357A DIN | D3 / GPIO5 |
| MAX98357A BCLK | D4 / GPIO6 |
| MAX98357A LRC | D5 / GPIO7 |
| Button 1 | D10 / GPIO10, active low |
| Button 2 | D9 / GPIO9, active low |
| Mute slider | D8 / GPIO8, active low |
| WS2812B ring DI | D7 / GPIO20 |

The default logical mapping is D10 = broadcast and D9 = reply. Set `buttons_swapped: true` for assemblies with the two physical buttons soldered in the opposite order.

## Behaviour

- Hold the broadcast button to send to every companion and device in the configured mesh group. Its ring animation is green.
- Hold reply to send to the most recent device/app that sent this device audio. Its animation is blue.
- Direct messages play only at the addressed destination; broadcasts play at every group member.
- A competing PTT press buffers up to 500 ms. If the floor is not released by then, the device leaves the active message playing and shows two quick red pulses.
- The mute slider silences received playback. It does not stop discovery or transmission.
- Received audio shows the speaking animation. A 24-pixel ring defaults to restrained brightness 48 to reduce shared-rail noise.

## Wire protocol

All peers independently implement the same explicit big-endian, fixed 32-byte `PTT1` UDP header and packet-independent IMA ADPCM codec:

- 16 kHz, mono, 16-bit PCM
- 20 ms / 320-sample frames
- 164-byte ADPCM payloads
- CLAIM ×3, 100 ms pre-audio delay, deterministic `(session_id, sender_id)` tie-break, 750 ms expiry, END drain
- 4-frame / 80 ms jitter prebuffer with small reorder window and attenuated replay PLC

`HELLO` discovery beacons go to `239.255.42.99:45678`, with a TTL of 1. Each beacon carries the alias, firmware version, and an explicit intercom-protocol revision (`IH1` discovery payload); the companion shows these values and flags incompatible revisions. A subnet broadcast copy is used only as a fallback for access points that suppress Wi-Fi multicast. Beacons repeat every three seconds and expire after ten seconds. All floor control and audio packets are then sent by UDP unicast to the learned active-peer snapshot; this avoids Wi-Fi multicast loss while retaining zero static IP configuration. Directed audio is unicast to its one target.

## Firmware

The firmware is ESP-IDF 5.4 compatible. The local production build used Windows ESP-IDF at `C:\Espressif\frameworks\esp-idf-v5.4.4` and COM9.

```bat
cd firmware
call C:\Espressif\frameworks\esp-idf-v5.4.4\export.bat
set "CMAKE_BUILD_PARALLEL_LEVEL=1"
idf.py build
idf.py -p COM9 flash
```

### USB configuration

The native USB serial/JTAG device accepts one JSON request per line and replies with a JSON configuration object. Wi‑Fi credentials are accepted when writing but deliberately never returned. A Wi‑Fi, ring brightness, or ring orientation change reboots after its acknowledgement.

```json
{"cmd":"get"}
{"cmd":"set","alias":"Reception","ssid":"ExampleWiFi","password":"…"}
{"cmd":"set","buttons_swapped":true,"ring_orientation":180}
{"cmd":"set","led_brightness":48,"speaker_volume":512}
```

`ring_orientation` is `0` (normal) or `180`, which shifts the logical animation centre by 12 LEDs. Device configuration is also exposed through unencrypted in-group UDP control messages as requested; use it only on a trusted local network.

### Firmware OTA

The initial OTA-capable build must be flashed over USB once. It installs a 4 MB
dual-slot layout (`ota_0`, `ota_1`, and `otadata`) and preserves NVS device
configuration. Future application firmware updates are wireless; bootloader,
partition table, and NVS are never updated over the air.

Build a signed package with the ignored release private key, then choose its
`.ota.json` manifest in the companion. The companion starts a temporary,
tokenised HTTP endpoint bound to the active LAN adapter; the device is only an
HTTP client. It accepts a download only when the URL host matches the UDP offer
sender, the image SHA-256 matches, and the signed manifest verifies against the
built-in public key. It writes the inactive slot, reboots, and commits only
after a healthy startup; otherwise ESP-IDF rolls back automatically.

```powershell
.\tools\ota\Create-OtaPackage.ps1 `
  -Image .\firmware\build\wifi_intercom.bin `
  -Version 0.7.1 `
  -PrivateKey .\.ota-keys\dev-release-key.pem
```

Keep the companion open during the update and allow its one-time Windows
Firewall prompt on the **private** LAN when asked. The device’s discovery row
shows `p1 / OTA` only after it has this bootstrap firmware.

## Companion app

The production companion is a .NET 10 Windows Forms application. It provides
discovery, aliases, broadcast/reply/selected-device PTT, selected audio devices,
USB Wi-Fi provisioning, remote configuration, and signed sequential OTA for
one selected or all OTA-capable active devices.

```powershell
dotnet run --project .\companion\IntercomCompanion\IntercomCompanion.csproj
```

The active-device list is populated automatically. Select a device to query its configuration, hold the purple control to direct-message it, or update its alias, volume, LED brightness, button swap and ring orientation. Space is a broadcast PTT shortcut. Broadcast messages fan out only while a peer holds the floor; idle traffic is discovery-only.

## Hardware note: shared amp/ring rail

The current prototype powers the MAX98357A and WS2812B ring from the same rail. The lower firmware brightness reduces the audible coupling, but a future board should use a bulk capacitor at the ring, a separate/star 5 V and ground return for the amplifier, and a short data path with a series resistor. This is a power-integrity concern, not an ADPCM/I²S defect.
