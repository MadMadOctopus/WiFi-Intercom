# Production intercom architecture

## Network model

`mesh_id` is a logical intercom group, not an ESP-MESH radio network. Devices
and companion applications use IPv4 multicast group `239.255.42.99:45678` only
for low-rate `HELLO` discovery on the existing 2.4 GHz LAN. Learned endpoint
addresses are held only in a short-lived peer table, so no IP list is stored in
configuration. The Wi-Fi radio remains awake while the unit is USB-powered.

Every group packet carries a stable sender ID. Broadcast control and RTP go to
active learned peers in the group. Directed control and RTP go only to the
selected endpoint. See the [current p3 contract](protocol-p3.md).

## Floor and PTT behaviour

There is one global broadcast floor per network group. Broadcast contenders
use the lowest `(session_id, sender_id)` tie-break and existing claim retries.
Directed sessions reserve their participating endpoints and require an ACCEPT;
unrelated directed sessions coexist with one another and with broadcasts.

Local priority is **local TX > directed RX > broadcast RX**. Directed reception
replaces broadcast playback locally while the broadcast owner remains active.
A local transmitter rejects incoming directed calls; an existing directed
receiver rejects a different directed caller. Busy nodes may miss broadcasts:
broadcast is best-effort to available nodes, with no catch-up playback.

Broadcast and reply buttons retain green/blue animations. Reply resolves the
most recent sender's stable ID through discovery. Broadcast PTT buffers up to
500 ms while the broadcast floor is occupied. Mute affects playout only.

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
| protocol revision (3) | 2 |

Audio remains 16 kHz, mono, 20 ms, packet-independent IMA ADPCM frames. The
packet types are `CLAIM`, `BUSY`, `AUDIO`, `END`, `HELLO`, `HEARTBEAT`,
`CONFIG_GET`, `CONFIG_SET`, `CONFIG_REPLY`, the existing signed OTA packet
types 10–12, and directed `ACCEPT` (13).

Assistant service/client capabilities occupy `0x02`/`0x04`; OTA keeps `0x01`.
NVS v3 adds `assistant_enabled` (default false) and `assistant_service_id`
(default 0/unset). The ID survives service absence and resolves dynamically;
permission and technical capability are distinct. No assistant interactions
are implemented. See [configuration details](protocol-p3.md#configuration-and-service-selection).
