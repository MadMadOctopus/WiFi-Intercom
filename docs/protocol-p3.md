# Protocol p3: broadcast floor, directed sessions and assistant administration

This is the current contract, superseding the p1/p2 floor and compatibility
rules in historical companion design documents. All deployment devices and
companions must be updated together. Audio codecs, frame sizes and ports are
unchanged. Nothing in this revision implements an assistant conversation.

## Ownership and local audio

Each network group has one global **broadcast floor**. Only broadcast
transmitters contend for it, using the existing three CLAIMs at 30 ms intervals,
100 ms pre-audio delay and lowest `(session_id, sender_id)` collision tie-break.
The companion's existing multiple-group support preserves a floor per group.
An all-groups broadcast contends in each selected group.

Directed CLAIM/ACCEPT/BUSY/HEARTBEAT/END travel only between their participating
endpoints. Directed RTP remains unicast. There is no global directed floor:
A → B, C → D and E → F can proceed together, and a broadcast can reach other
available receivers at the same time. Each node has one local audio resource.

Local priority is **local TX > directed RX > broadcast RX**:

- A directed CLAIM interrupts broadcast reception locally, clears its queued
  playback and reserves the receiver for the directed sender. Other receivers
  and the broadcaster continue. Broadcast ownership is tracked independently
  of the local playback session.
- A node in directed TX/RX ignores broadcast media. Broadcast control still
  maintains its knowledge of the broadcast owner.
- A directed CLAIM received during local claiming/transmission is rejected
  with directed BUSY, regardless of the arbitration tuple. A second directed
  sender cannot take an existing directed receiver either.
- Intentional local PTT can leave reception. Broadcast PTT still waits for an
  occupied broadcast floor, retaining at most 500 ms of microphone audio.
  An unsuccessful directed attempt is indicated as busy/unavailable; release
  and press again to call another endpoint. Crossed directed attempts may both
  be rejected, or one may succeed on a retry after the other was rejected.

Broadcast delivery is best-effort to currently available receivers. Busy nodes
may legitimately miss it; there is no replay or automatic catch-up when they
become free. This is a home intercom, not alarm-grade delivery.

## Wire contract

The 32-byte big-endian `PTT1` header retains its layout. Bytes 30–31, formerly
reserved, now contain the uint16 protocol revision **3**. All non-HELLO
control/configuration/OTA packets with another revision are rejected before
state changes. HELLO remains self-describing; the companion may display older
announcements as incompatible, but does not use them for PTT/configuration/OTA.
Firmware only registers supported HELLO peers. No mixed-revision audio
interoperability is attempted.

`flags & 0x01` means directed for CLAIM, ACCEPT, BUSY, HEARTBEAT and END.
Broadcast controls have this bit clear. Packet types 1–12 retain their numbers;
**ACCEPT = 13** acknowledges a directed receiver reservation. It has no payload
and echoes the caller's session ID. Directed BUSY also echoes the rejected
caller's session ID. The caller accepts those responses only from its selected
sender ID/current endpoint and for its current directed claim.

The directed caller sends the usual three CLAIMs and waits through the 100 ms
claim window. It sends media only if ACCEPT arrived. The receiver acknowledges
repeated claims for the same `(sender_id, session_id)` idempotently without
resetting playback. If ACCEPT is lost, the caller fails closed; the orphaned
reservation expires after 750 ms. No global state is changed by directed control.

Broadcast BUSY retains its prior occupied-session/tie-break interpretation
(session zero denotes a busy OTA target); it is never applied to directed TX.
Both kinds of transmitter now send HEARTBEAT every 100 ms, so nodes that skip
broadcast media still know who owns the floor. HEARTBEAT/RTP refresh only the
matching reservation/owner. END matches sender, session and directed scope;
a broadcast END cannot drain a directed session. END remains repeated three
times with a bounded playback drain. Missing END is covered by the 750 ms
inactivity timeout. RTP reception checks the session SSRC and source address.

## Discovery capabilities

IH3 remains:

```
"IH3" | protocol(1) | capabilities(1) | flags(1) | firmware_len(1) | firmware | alias
```

| Mask | Capability | Meaning |
| --- | --- | --- |
| `0x01` | OTA | Existing signed OTA target support, unchanged |
| `0x02` | ASSISTANT_SERVICE | Provides a service addressable over directed intercom transport |
| `0x04` | ASSISTANT_CLIENT | Technically supports initiating assistant interactions |
| `0x08`–`0x80` | Unassigned | Preserved/queryable and ignored by code that does not understand them |

Capability describes technical support, not administrator permission. Production
C3 advertises OTA only. Future firmware can supply `INTERCOM_CAPABILITIES` when
it actually implements the corresponding roles. The Windows companion
understands both assistant bits and advertises neither. Existing mute/talking
HELLO flags are unchanged; talking includes directed TX.

## Configuration and service selection

Remote ConfigGet/ConfigSet and USB line JSON share the existing field set plus:

| Field | JSON type | Default | Meaning |
| --- | --- | --- | --- |
| `assistant_enabled` | bool | false | Administrator permission for this device |
| `assistant_service_id` | uint32 integer | 0 | Selected stable sender ID; zero means unset |

Writes reject wrong types, fractions and out-of-range service IDs atomically.
Omitted fields preserve existing values, and unknown fields keep the existing
ignore convention. Settings apply without a restart and persist in NVS v3.
Migration from frozen v1/v2 layouts preserves existing settings and explicitly
leaves assistance disabled/unset, including when old struct padding is nonzero.
Reads return both fields. Wi-Fi passwords are accepted on write and never
returned. USB, remote writes and physical-slider soft-mute clearing serialize
NVS transactions without holding the audio state mutex during flash IO.

A future interaction must require client capability **and** permission **and**
an available configured service. Neither saving permission nor advertising
capability implements such an interaction. Enabling with an unset/offline ID is
retained administratively but unavailable operationally.

Configure device offers **Enable voice assistant** and an **Assistant service**
selector only for compatible ASSISTANT_CLIENT peers. Service options contain
compatible, discovered ASSISTANT_SERVICE peers in the device's group. Labels
show alias and sender ID; the value sent to the device is the uint32 sender ID.
An offline saved ID appears as unavailable and is preserved. Group moves also
retain the ID; the service must be reachable in the new group to be available.

Firmware `find_active_peer_locked(id, required_capabilities)` and companion
`AssistantServices.Resolve` resolve stable IDs through the current discovery
registry. No IP, alias, hostname or magic ID identifies an assistant. If that
exact service disappears, keep its ID and report unavailable; never fall back
silently to another discovered service.

Wake words, VAD, STT, TTS, assistant initiation/server, LLM and calendar features
are not implemented.

## Verification

See [p3 validation](p3-validation.md) for local commands, results and physical
checks that remain untested. Tests use production protocol/configuration code
and the production companion state machine, with network/audio/NVS boundaries
substituted on the host. They do not establish physical timing or audio quality.
