/* Public half of the development/release signing key. It is safe to ship in
 * firmware; the corresponding private key is deliberately ignored by git and
 * must be held by the release operator. Replace this value before production
 * manufacturing with the organisation's offline release public key. */
#pragma once

#define INTERCOM_OTA_PUBLIC_KEY_PEM \
    "-----BEGIN PUBLIC KEY-----\n" \
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEEisSIeNyz/nprL27lHmXjqS5GlJz\n" \
    "vU0hygcTNvyz9seKIekc89pXbeVtZioyCGsF4+tM5/khTXjTsqhjljXsfQ==\n" \
    "-----END PUBLIC KEY-----\n"
