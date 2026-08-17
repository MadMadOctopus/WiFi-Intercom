# WiFi-Intercom — Project Wiki

A curated knowledge base for this repository. Each article documents a durable
piece of the system — architecture, workflows, integrations, conventions —
discovered while working on the codebase. Transient task notes, in-flight bugs,
and PR-specific context do **not** belong here.

> Wiki content is treated as public. Never commit secrets, credentials,
> customer data, or internal hostnames into these files.

Last updated: 2026-08-17

## How to use this wiki

- Browse the **Documentation files** section below to find a topic.
- Each article is self-contained; read articles, not the whole wiki.
- File names use a category prefix (`architecture-`, `feature-`, `api-`, etc.)
  so related articles cluster together alphabetically.

## Documentation files

### Architecture

- [**Firmware concurrency model**](./architecture-firmware-concurrency.md) — FreeRTOS tasks, their stack budgets, and the `g_lock` mutex discipline in the ESP32 node.
- [**System overview**](./architecture-system-overview.md) — the two halves of the product (ESP-IDF firmware node and WinForms companion), the PTT1 UDP protocol, and where each subsystem lives.

### Security

- [**Network trust model**](./security-network-trust-model.md) — what a packet must present to be accepted, why control packets are forgeable on the LAN, and how OTA offers are the one signed surface.
