#pragma once

#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>

#include "lwip/sockets.h"

/* All status packets are sent as ordinary, addressed PTT1 control packets by
 * app_main. The OTA component owns no listening socket or server. */
typedef void (*ota_status_callback_t)(const struct sockaddr_in *destination,
                                      uint32_t session,
                                      const char *state,
                                      int progress,
                                      const char *message);

/* Runs after an offer's manifest signature has verified. The callback must
 * atomically test that the node's floor is idle and, if so, call
 * ota_manager_set_active() before releasing whatever lock guards the floor
 * state, so no PTT can begin between the check and the claim. Returns whether
 * the floor was claimed. */
typedef bool (*ota_claim_floor_t)(void);

void ota_manager_init(ota_status_callback_t callback, ota_claim_floor_t claim_floor);

/* Copies an OTA offer and hands it to the OTA task, which parses and
 * authenticates it: the manifest signature check needs more stack than a
 * network receive task provides. Returning true therefore only means the offer
 * was admitted; a manifest rejected later is reported through the status
 * callback. The URL must be HTTP to the offer sender's IPv4 address; image
 * authentication is performed by the signed manifest and streamed SHA-256
 * verification. Only one offer is admitted at a time, and the caller may hold
 * its own state lock across this call - it never blocks. The OTA-active state
 * is claimed only after the signature verifies (via the claim callback), so
 * unauthenticated offers can never suppress PTT. */
bool ota_manager_offer(const uint8_t *payload, size_t payload_len,
                       const struct sockaddr_in *source, uint32_t session,
                       const char **reject_reason);

bool ota_manager_is_active(void);

/* Only for use inside the ota_claim_floor_t callback, while the caller's floor
 * lock is held. */
void ota_manager_set_active(void);
bool ota_manager_has_recent_error(void);

/* Call only once networking/audio/control have started successfully. With
 * bootloader rollback enabled this commits a newly booted pending image. */
void ota_manager_mark_running_valid(void);
