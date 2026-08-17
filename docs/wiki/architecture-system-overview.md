# System Overview

## Overview
WiFi-Intercom is a two-part product: a battery/USB intercom node built on the
XIAO ESP32-S3 (ESP-IDF firmware) and a Windows companion app (.NET 10
WinForms). Both speak the same custom UDP protocol ("PTT1" framing) over the
LAN, so a companion instance is a full peer of the hardware nodes: it is
discovered, can claim the floor, send and receive audio, and push OTA updates.

## Where it lives
- `firmware/main/` — the ESP-IDF node. `app_main.c` holds the state machine,
  FreeRTOS tasks and jitter buffer; `protocol.c/h` the wire format;
  `device_config.c/h` NVS-backed settings; `ota_manager.c/h` firmware updates;
  `adpcm.c/h` the codec; `ring_controller.cpp` the LED ring; `usb_control.c`
  the USB configuration channel.
- `companion/IntercomCompanion/` — the WinForms app. `Core/` is the protocol
  and session layer (`IntercomNode.cs` socket + discovery + dispatch,
  `ReceiveSession.cs` floor control and playback session, `Protocol.cs` packet
  parsing, `Peer.cs` peer records), `Audio/` the codec and jitter buffer
  (`ImaAdpcm.cs`, `JitterBuffer.cs`, `AudioEngine.cs`), `Views/` the UI.
- `docs/design/companion-redesign-spec.md` — binding spec for behaviour and
  the p1→p2 protocol uplift; `CLAUDE.md` at the repo root lists non-negotiable
  UI rules for the companion.

## How it works
- Transport is UDP multicast group `239.255.42.99` on port `45678`
  (companion constants: `IntercomNode.MulticastGroup`, `Protocol.Port`).
- Discovery is a HELLO chain (`IH1`/`IH2`/`IH3` versions); `IH3` carries the
  p2 fields (alias, capabilities, flags such as soft/hardware mute, talking).
  Peers are identified by a 32-bit node id plus a 4-character mesh id.
- Audio is push-to-talk with floor arbitration: a node sends a claim, may be
  rejected with a Busy packet, then streams IMA-ADPCM frames in RTP-style
  framing (16-bit sequence numbers, 20 ms frames, 50 packets/s, 640-byte PCM
  frames after decode). Both sides run a jitter buffer keyed by the 16-bit
  sequence; sequence comparison must be wrap-aware.
- End of transmission is signalled by an END packet; receivers also fall back
  to an RX timeout if the END packet is lost.
- OTA: the companion serves the firmware artifact (`Core/OtaArtifactServer.cs`)
  and sends a signed offer; the node verifies the ECDSA signature against the
  key baked into `firmware/main/ota_public_key.h` before flashing. OTA
  eligibility on the companion side is `Peer.IsOtaEligible` (exact protocol
  version match + OTA capability bit), not the capability bit alone.

## Constraints and gotchas
- A p2 node must stay interoperable with p1 nodes for audio and discovery; the
  codec, frame size and ports must not change (repo `CLAUDE.md`, Firmware
  section).
- `Peer.IsLegacy` (`ProtocolVersion is null or 1`) is the single legacy check;
  do not re-derive it inline in UI code.
- Sample devices shown in the design prototype are illustrative; never
  hard-code them.

## Related
- [Firmware concurrency model](./architecture-firmware-concurrency.md)
- [Network trust model](./security-network-trust-model.md)
