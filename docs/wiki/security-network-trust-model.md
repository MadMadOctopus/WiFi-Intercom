# Network Trust Model

## Overview
The intercom protocol is designed for a trusted LAN segment. Knowing what a
packet must present to be accepted — and what it does not have to present —
is essential when adding any new packet handler on either side.

## How it works
- Packet admission is intentionally thin: a packet is accepted if it carries
  the protocol magic bytes and the mesh id (4 characters). There is no
  authentication, encryption, or replay protection on regular control and
  audio packets. See `Protocol.TryParse` in
  `companion/IntercomCompanion/Core/Protocol.cs` and the dispatch in
  `firmware/main/app_main.c` (`net_rx_task`).
- The mesh id and node ids are broadcast in cleartext HELLO packets, so any
  host on the LAN can learn everything needed to craft accepted packets.
  Consequence: every control packet (Busy, claim, config, OTA status) is
  forgeable, and handlers must validate context — sender id and session id
  against the current session state — before acting, because the transport
  layer proves nothing about the sender.
- Payload parsing is part of the attack surface: JSON payloads arrive from
  the network, so parsers on both sides must survive syntactically valid but
  type-mismatched values without taking down the receive path.
- The one cryptographically protected surface is OTA: an OTA offer carries an
  ECDSA signature that the node verifies (mbedtls) against the public key
  compiled into `firmware/main/ota_public_key.h` before any download or
  flash. Signature verification is deliberately heavy (base64 + pk_parse +
  pk_verify) and must run on a task with a large enough stack (`ota_task`),
  since even a rejected offer costs the full verification.

## Constraints and gotchas
- Never assume a packet type "can only come from" a particular peer; check.
- Denial-of-service via crafted-but-parseable packets is the realistic threat
  class here (kill a receive loop, cancel someone's floor claim, exhaust a
  small task stack) — new handlers should be reviewed against exactly those
  three failure modes.
- Do not extend the wire format in a way that breaks p1 interoperability;
  security additions must live inside existing payloads or new packet types.

## Related
- [System overview](./architecture-system-overview.md)
- [Firmware concurrency model](./architecture-firmware-concurrency.md)
