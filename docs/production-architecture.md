# Production intercom architecture

## Network model

`mesh_id` is a logical intercom group, not an ESP-MESH radio network. Devices
and companion applications join IPv4 multicast group `239.255.42.99:45678` on
the existing 2.4 GHz LAN. Multicast keeps the group self-discovering and avoids
any configured peer IP list. The Wi-Fi radio remains awake while the unit is
USB-powered.

Every group packet carries a sender ID. Broadcast sessions use the multicast
endpoint. Directed sessions use the target endpoint learned at runtime from
`HELLO`; no device IP is stored in configuration. Their `CLAIM` and lightweight
`HEARTBEAT` packets remain multicast, so every peer observes one shared,
deterministic floor, while only the intended endpoint receives audio.

## Floor and PTT behaviour

* A `CLAIM` is multicast before a sender streams audio. The smallest
  `(session_id, sender_id)` wins a simultaneous claim.
* Button 1 creates a broadcast session and shows the green talk animation.
* Button 2 targets the sender of the last received message and shows the blue
  talk animation. It does nothing until a sender is known.
* While another session owns the floor, a held PTT retains at most 25 frames
  (500 ms) of microphone audio. If the remote session ends within that window,
  that retained audio is sent first. Otherwise the attempt is rejected with two
  short red pulses, while remote playout continues.
* The mute switch prevents I2S playout only; it never removes a unit from
  discovery or floor control.

## Discovery and configuration

Peers send a `HELLO` at startup and every three seconds. It carries the device
ID, alias, capabilities, and firmware revision, allowing companion apps to
show active devices without a central service. A 10-second absence expires a
peer.

Device configuration is stored in NVS. USB uses a line-delimited JSON control
channel over the XIAO's USB Serial/JTAG interface. Network configuration uses
the same fields inside addressed `CONFIG_GET`, `CONFIG_SET`, and
`CONFIG_REPLY` packets. The group intentionally has no authentication, as
requested; it is therefore suitable only for a trusted LAN.

## Wire protocol

All integer fields are big-endian. The proven 32-byte header is retained:

| Field | Size |
| --- | ---: |
| magic (`PTT1`) | 4 |
| type | 1 |
| flags | 1 |
| header length | 2 |
| mesh ID | 4 |
| sender ID | 4 |
| session ID | 4 |
| sequence | 4 |
| timestamp (ms) | 4 |
| payload length | 2 |
| reserved | 2 |

Audio remains 16 kHz, mono, 20 ms, packet-independent IMA ADPCM frames. The
packet types are `CLAIM`, `BUSY`, `AUDIO`, `END`, `HELLO`, `HEARTBEAT`,
`CONFIG_GET`, `CONFIG_SET`, and `CONFIG_REPLY`.
