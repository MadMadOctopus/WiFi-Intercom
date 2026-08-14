# Wi-Fi Intercom

Wi-Fi Intercom is a USB-powered push-to-talk system for Seeed XIAO ESP32-C3
devices and a Windows companion application. It is designed for a small,
trusted Wi-Fi LAN: devices discover each other automatically, with no server
and no configured peer-IP list.

Start with the [user manual](docs/User-Manual.md) to configure and operate a
device. The sections below are the technical reference for builders and
maintainers.

## Repository layout

- `firmware/` — ESP-IDF firmware for the XIAO ESP32-C3.
- `companion/IntercomCompanion/` — .NET Windows Forms companion app.
- `tools/ota/` — local packaging and diagnostic helpers for signed OTA
  releases.

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

The default logical mapping is D10 = broadcast and D9 = reply. Set
`buttons_swapped: true` for assemblies with the two physical buttons wired in
the opposite order. `ring_orientation: 180` rotates the logical ring centre by
12 LEDs.

## Device behaviour

- Hold **broadcast** to send to all active companions and devices in the same
  network group. The ring uses the green talking animation.
- Hold **reply** to send to the most recent sender. The ring uses the blue
  talking animation.
- The mute slider silences received playback only; discovery and transmission
  continue.
- Received broadcast and correctly addressed directed messages play
  immediately and show the speaking animation.
- When another sender owns the floor, a pressed PTT button retains up to
  500 ms of microphone audio. If the floor becomes free in time, transmission
  begins; otherwise the active message continues and the device gives two red
  error pulses.

## Network and audio design

This is a LAN group, not a Wi-Fi mesh-routing protocol. Every node must join
the same reachable 2.4 GHz Wi-Fi network and use the same `mesh_id` (the
default is `MESH`). There is no authentication or encryption for intercom
control/configuration, so use a trusted private LAN.

Discovery sends compact `PTT1` `HELLO` beacons to `239.255.42.99:45678` (TTL
1), with subnet broadcast and low-rate unicast probing as fallbacks for access
points that suppress client multicast. Discovery beacons use the `IH2` payload
format and announce the alias, firmware version, protocol revision and OTA
capability. After discovery, control and media are sent directly to each
active peer; no static IP address is retained.

PTT floor control uses an explicit, big-endian 32-byte `PTT1` UDP control
header. It uses CLAIM ×3, a 100 ms pre-audio delay, deterministic
`(session_id, sender_id)` tie-break, a 750 ms release timeout, and END drain.
Audio travels as RTP on UDP port 45679 with payload type 96: 16 kHz mono
packet-independent IMA ADPCM, 320 samples / 20 ms per frame and a 164-byte
payload. The receiver starts audio after a 20-frame / 400 ms prebuffer and has
reorder plus attenuated replay packet-loss concealment. Voice packets are
marked Wi-Fi WMM video priority where the platform supports it.

## Build and flash firmware

The firmware targets ESP-IDF 5.4. The production setup uses the Windows
installation at `C:\Espressif\frameworks\esp-idf-v5.4.4`.

```bat
cd firmware
call C:\Espressif\frameworks\esp-idf-v5.4.4\export.bat
set "CMAKE_BUILD_PARALLEL_LEVEL=1"
idf.py build
idf.py -p COM9 flash
```

The production partition table is for a 4 MB flash and has two application
slots. A USB flash writes the bootloader, partition table, OTA metadata and
application, but leaves NVS configuration (including Wi-Fi credentials) in
place. A first USB flash of an OTA-capable build is required before wireless
updates can be used.

## USB configuration protocol

The native USB Serial/JTAG connection accepts one JSON request per line and
returns one JSON configuration object per line. Wi-Fi passwords are accepted
when writing but intentionally never returned.

```json
{"cmd":"get"}
{"cmd":"set","alias":"Reception","ssid":"ExampleWiFi","password":"…"}
{"cmd":"set","buttons_swapped":true,"ring_orientation":180}
{"cmd":"set","led_brightness":48,"speaker_volume":512}
```

Valid ranges: `speaker_volume` is 64–1024 and `led_brightness` is 0–255.
Changing Wi-Fi credentials, ring brightness, button mapping or ring
orientation acknowledges first and then restarts the device. Alias and volume
take effect without a restart.

## Companion application

The companion is a .NET 10 Windows Forms app. It discovers devices, provides
Windows recording/playback device selection, PTT broadcast/reply/targeted
calls, USB Wi-Fi provisioning, remote device configuration, and signed OTA.

```powershell
dotnet run --project .\companion\IntercomCompanion\IntercomCompanion.csproj
```

See the [user manual](docs/User-Manual.md) for operating instructions.

## Signed firmware OTA

OTA updates only the inactive application slot; it never updates the
bootloader, partition table or NVS. The companion checks the SHA-256 and
ECDSA signature before serving the image from a temporary, tokenised local HTTP
endpoint. The device accepts the offer only from its UDP sender's IP address,
verifies the same signature and streamed hash while writing, then reboots into
the inactive slot. It marks the new firmware healthy after startup; ESP-IDF
rolls back if that does not happen.

Create a package after building the image:

```powershell
.\tools\ota\Create-OtaPackage.ps1 `
  -Image .\firmware\build\wifi_intercom.bin `
  -Version 0.7.8 `
  -PrivateKey .\.ota-keys\dev-release-key.pem
```

The private release key is deliberately ignored by Git. Before manufacturing,
replace the public key in both `firmware/main/ota_public_key.h` and
`companion/IntercomCompanion/Core/OtaPackage.cs`; see
[tools/ota/README.md](tools/ota/README.md).

## Hardware note: shared amp/ring rail

The current prototype powers the MAX98357A and WS2812B ring from the same
rail. The default restrained ring brightness reduces audible coupling, but a
future board should use a bulk capacitor at the ring, a separate/star 5 V and
ground return for the amplifier, and a short ring data path with a series
resistor. This is a power-integrity concern rather than an ADPCM or I2S defect.
