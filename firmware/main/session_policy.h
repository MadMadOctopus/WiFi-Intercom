#pragma once
#include <stdbool.h>
#include <stdint.h>

/* Pure state shared by firmware handlers and host simulations. Caller locks. */
typedef struct {
    uint32_t sender, session, last_ms;
    bool active;
} broadcast_floor_t;
static inline bool session_precedes(uint32_t session, uint32_t sender,
                                    uint32_t other_session, uint32_t other_sender)
{
    return session < other_session || (session == other_session && sender < other_sender);
}
static inline void broadcast_expire(broadcast_floor_t *floor, uint32_t now)
{
    if (floor->active && now - floor->last_ms > 750) floor->active = false;
}
static inline bool broadcast_matches(const broadcast_floor_t *floor, uint32_t sender, uint32_t session)
{
    return floor->active && floor->sender == sender && floor->session == session;
}
static inline bool broadcast_claim(broadcast_floor_t *floor, uint32_t sender, uint32_t session, uint32_t now)
{
    broadcast_expire(floor, now);
    if (floor->active && !broadcast_matches(floor, sender, session) &&
        !session_precedes(session, sender, floor->session, floor->sender)) return false;
    *floor = (broadcast_floor_t){sender, session, now, true};
    return true;
}
/* A directed CLAIM only reserves this receiver. Repeated claims are idempotent. */
static inline bool directed_accept(bool local_tx, bool receiving, bool rx_directed,
                                   uint32_t rx_sender, uint32_t rx_session,
                                   uint32_t sender, uint32_t session)
{
    return !local_tx && (!receiving || !rx_directed ||
                        (rx_sender == sender && rx_session == session));
}
