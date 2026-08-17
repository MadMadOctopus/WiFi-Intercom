#include "ota_manager.h"

#include <stdio.h>
#include <string.h>

#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"
#include "esp_app_desc.h"
#include "esp_http_client.h"
#include "esp_log.h"
#include "esp_ota_ops.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "cJSON.h"
#include "lwip/inet.h"
#include "mbedtls/base64.h"
#include "mbedtls/pk.h"
#include "mbedtls/sha256.h"

#include "config.h"
#include "ota_public_key.h"

#define OTA_URL_MAX 220
#define OTA_VERSION_MAX 31
#define OTA_SIGNATURE_MAX 128
#define OTA_DOWNLOAD_BUFFER 1024
#define OTA_OFFER_MAX 320

static const char *TAG = "ota";

typedef struct {
    char url[OTA_URL_MAX];
    char version[OTA_VERSION_MAX + 1];
    char sha256[65];
    char signature[OTA_SIGNATURE_MAX + 1];
    uint32_t size;
    uint8_t protocol;
    struct sockaddr_in source;
    uint32_t session;
} ota_offer_t;

/* The raw offer as it arrived, copied off the receiving task's buffer. Parsing
 * and signature verification happen on ota_task, which owns enough stack for
 * mbedtls; the receiving task only performs this copy. */
typedef struct {
    struct sockaddr_in source;
    uint32_t session;
    uint16_t payload_len;
    uint8_t payload[OTA_OFFER_MAX];
} ota_request_t;

static QueueHandle_t s_queue;
static ota_status_callback_t s_status;
static volatile bool s_active;
static volatile uint32_t s_error_until_ms;

static uint32_t now_ms(void) { return (uint32_t)(esp_timer_get_time() / 1000); }

static void report(const ota_offer_t *offer, const char *state, int progress,
                   const char *message)
{
    if (s_status) s_status(&offer->source, offer->session, state, progress, message);
}

static bool copy_string(cJSON *root, const char *name, char *out, size_t capacity)
{
    cJSON *value = cJSON_GetObjectItemCaseSensitive(root, name);
    if (!cJSON_IsString(value) || value->valuestring == NULL) return false;
    size_t length = strnlen(value->valuestring, capacity);
    if (length == 0 || length >= capacity) return false;
    memcpy(out, value->valuestring, length);
    out[length] = '\0';
    return true;
}

static bool is_lower_hex_sha256(const char *value)
{
    if (strlen(value) != 64) return false;
    for (int i = 0; i < 64; ++i) {
        char c = value[i];
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
    }
    return true;
}

static bool url_matches_source(const char *url, const struct sockaddr_in *source)
{
    static const char scheme[] = "http://";
    if (strncmp(url, scheme, sizeof(scheme) - 1) != 0) return false;
    const char *host_start = url + sizeof(scheme) - 1;
    const char *path = strchr(host_start, '/');
    if (!path || path == host_start) return false;
    const char *port = strchr(host_start, ':');
    if (port && port > path) port = NULL;
    const char *host_end = port ? port : path;
    size_t host_length = (size_t)(host_end - host_start);
    if (host_length == 0 || host_length >= 16) return false;
    char host[16] = {0};
    memcpy(host, host_start, host_length);
    struct in_addr address;
    return inet_aton(host, &address) != 0 && address.s_addr == source->sin_addr.s_addr;
}

static bool build_signed_message(const ota_offer_t *offer, char *out, size_t capacity)
{
    int n = snprintf(out, capacity, "wifi-intercom-ota-1\n%s\n%u\n%lu\n%s\n",
                     offer->version, (unsigned)offer->protocol,
                     (unsigned long)offer->size, offer->sha256);
    return n > 0 && (size_t)n < capacity;
}

static bool verify_offer_signature(const ota_offer_t *offer)
{
    char message[160];
    uint8_t digest[32];
    uint8_t signature[96];
    size_t signature_length = 0;
    if (!build_signed_message(offer, message, sizeof(message)) ||
        mbedtls_sha256((const unsigned char *)message, strlen(message), digest, 0) != 0 ||
        mbedtls_base64_decode(signature, sizeof(signature), &signature_length,
                              (const unsigned char *)offer->signature,
                              strlen(offer->signature)) != 0)
        return false;

    mbedtls_pk_context key;
    mbedtls_pk_init(&key);
    int result = mbedtls_pk_parse_public_key(&key,
        (const unsigned char *)INTERCOM_OTA_PUBLIC_KEY_PEM,
        strlen(INTERCOM_OTA_PUBLIC_KEY_PEM) + 1);
    if (result == 0)
        result = mbedtls_pk_verify(&key, MBEDTLS_MD_SHA256, digest, sizeof(digest),
                                   signature, signature_length);
    mbedtls_pk_free(&key);
    return result == 0;
}

static int compare_versions(const char *left, const char *right)
{
    unsigned l[3] = {0}, r[3] = {0};
    if (sscanf(left, "%u.%u.%u", &l[0], &l[1], &l[2]) < 2 ||
        sscanf(right, "%u.%u.%u", &r[0], &r[1], &r[2]) < 2) return -1;
    for (int i = 0; i < 3; ++i) {
        if (l[i] != r[i]) return l[i] > r[i] ? 1 : -1;
    }
    return 0;
}

static bool parse_offer(const uint8_t *payload, size_t payload_len,
                        const struct sockaddr_in *source, uint32_t session,
                        ota_offer_t *offer, const char **reason)
{
    if (payload_len == 0 || payload_len > 320) { *reason = "invalid offer"; return false; }
    char json[321] = {0};
    memcpy(json, payload, payload_len);
    cJSON *root = cJSON_Parse(json);
    if (!root) { *reason = "invalid JSON"; return false; }
    bool valid = copy_string(root, "url", offer->url, sizeof(offer->url)) &&
                 copy_string(root, "version", offer->version, sizeof(offer->version)) &&
                 copy_string(root, "sha256", offer->sha256, sizeof(offer->sha256)) &&
                 copy_string(root, "signature", offer->signature, sizeof(offer->signature));
    cJSON *protocol = cJSON_GetObjectItemCaseSensitive(root, "protocol");
    cJSON *size = cJSON_GetObjectItemCaseSensitive(root, "size");
    if (!valid || !cJSON_IsNumber(protocol) || !cJSON_IsNumber(size) ||
        protocol->valueint < 1 || protocol->valueint > 255 ||
        size->valuedouble <= 0 || size->valuedouble > 0x170000) {
        cJSON_Delete(root); *reason = "invalid manifest"; return false;
    }
    offer->protocol = (uint8_t)protocol->valueint;
    offer->size = (uint32_t)size->valuedouble;
    offer->source = *source;
    offer->session = session;
    cJSON_Delete(root);
    if (offer->protocol != INTERCOM_PROTOCOL_VERSION) { *reason = "protocol mismatch"; return false; }
    if (!is_lower_hex_sha256(offer->sha256)) { *reason = "invalid hash"; return false; }
    if (!url_matches_source(offer->url, source)) { *reason = "URL source mismatch"; return false; }
    if (compare_versions(offer->version, esp_app_get_description()->version) <= 0) {
        *reason = "not a newer firmware"; return false;
    }
    if (!verify_offer_signature(offer)) { *reason = "signature rejected"; return false; }
    return true;
}

static bool sha_matches(const uint8_t digest[32], const char *expected)
{
    static const char hex[] = "0123456789abcdef";
    for (int i = 0; i < 32; ++i)
        if (expected[2 * i] != hex[digest[i] >> 4] ||
            expected[2 * i + 1] != hex[digest[i] & 15]) return false;
    return true;
}

static void run_update(const ota_offer_t *offer)
{
    const esp_partition_t *partition = esp_ota_get_next_update_partition(NULL);
    esp_ota_handle_t handle = 0;
    esp_http_client_handle_t client = NULL;
    bool began = false;
    bool success = false;
    uint32_t received = 0;
    uint8_t buffer[OTA_DOWNLOAD_BUFFER];
    mbedtls_sha256_context sha;
    mbedtls_sha256_init(&sha);

    report(offer, "accepted", 0, "Downloading firmware");
    /* Do not erase the complete 1.5 MiB slot before opening the HTTP
     * connection. On the C3 that long, uninterrupted preparation caused
     * the watchdog to reset the unit before the first download request.
     * The image arrives in order, so ESP-IDF can safely erase each 4 KiB
     * sector immediately before its first write instead. */
    if (!partition || esp_ota_begin(partition, OTA_WITH_SEQUENTIAL_WRITES, &handle) != ESP_OK) {
        report(offer, "failed", 0, "Cannot open inactive OTA slot");
        goto complete;
    }
    began = true;
    if (mbedtls_sha256_starts(&sha, 0) != 0) {
        report(offer, "failed", 0, "SHA-256 initialisation failed");
        goto complete;
    }
    esp_http_client_config_t config = {
        .url = offer->url, .timeout_ms = 10000, .buffer_size = OTA_DOWNLOAD_BUFFER,
        .buffer_size_tx = 256, .disable_auto_redirect = true, .keep_alive_enable = false,
    };
    client = esp_http_client_init(&config);
    if (!client || esp_http_client_open(client, 0) != ESP_OK) {
        report(offer, "failed", 0, "Firmware download unavailable");
        goto complete;
    }
    int64_t content_length = esp_http_client_fetch_headers(client);
    if (esp_http_client_get_status_code(client) != 200 ||
        content_length != (int64_t)offer->size) {
        report(offer, "failed", 0, "Firmware download unavailable");
        goto complete;
    }
    int last_progress = -1;
    while (received < offer->size) {
        int read = esp_http_client_read(client, (char *)buffer,
                                         (int)((offer->size - received) < sizeof(buffer)
                                             ? (offer->size - received) : sizeof(buffer)));
        if (read <= 0 || esp_ota_write(handle, buffer, read) != ESP_OK ||
            mbedtls_sha256_update(&sha, buffer, read) != 0) {
            report(offer, "failed", (int)(received * 100 / offer->size), "Download write failed");
            goto complete;
        }
        received += (uint32_t)read;
        int progress = (int)(received * 100 / offer->size);
        if (progress >= last_progress + 5 || progress == 100) {
            last_progress = progress;
            report(offer, "downloading", progress, "Writing inactive firmware slot");
        }
    }
    uint8_t digest[32];
    if (mbedtls_sha256_finish(&sha, digest) != 0 || !sha_matches(digest, offer->sha256)) {
        report(offer, "failed", 100, "Firmware SHA-256 mismatch");
        goto complete;
    }
    if (esp_ota_end(handle) != ESP_OK || esp_ota_set_boot_partition(partition) != ESP_OK) {
        report(offer, "failed", 100, "Firmware image validation failed");
        goto complete;
    }
    began = false; /* esp_ota_end owns the handle from here. */
    success = true;
    report(offer, "rebooting", 100, "Verified; rebooting into new firmware");

complete:
    if (client) { esp_http_client_close(client); esp_http_client_cleanup(client); }
    mbedtls_sha256_free(&sha);
    if (began) esp_ota_abort(handle);
    if (!success) {
        s_error_until_ms = now_ms() + 1500;
        s_active = false;
        return;
    }
    vTaskDelay(pdMS_TO_TICKS(300));
    esp_restart();
}

/* Verification runs here, not in the caller: mbedtls base64, key parsing and
 * signature checking need far more stack than the network receive task owns.
 * s_active is already claimed by ota_manager_offer, so a rejected offer must
 * release it again. */
static void ota_task(void *arg)
{
    ota_request_t request;
    while (xQueueReceive(s_queue, &request, portMAX_DELAY) == pdTRUE) {
        ota_offer_t offer = {0};
        const char *reason = "invalid offer";
        if (!parse_offer(request.payload, request.payload_len, &request.source,
                         request.session, &offer, &reason)) {
            if (s_status)
                s_status(&request.source, request.session, "rejected", 0, reason);
            s_error_until_ms = now_ms() + 1500;
            s_active = false;
            continue;
        }
        run_update(&offer);
    }
}

void ota_manager_init(ota_status_callback_t callback)
{
    s_status = callback;
    s_queue = xQueueCreate(1, sizeof(ota_request_t));
    configASSERT(s_queue);
    xTaskCreate(ota_task, "ota", 7168, NULL, 3, NULL);
}

bool ota_manager_offer(const uint8_t *payload, size_t payload_len,
                       const struct sockaddr_in *source, uint32_t session,
                       const char **reject_reason)
{
    /* Staging slot rather than a local: s_active makes this function
     * single-entry, and the caller's task stack stays untouched. */
    static ota_request_t staged;
    if (payload_len == 0 || payload_len > OTA_OFFER_MAX) {
        if (reject_reason) *reject_reason = "invalid offer";
        return false;
    }
    if (s_active) {
        if (reject_reason) *reject_reason = "OTA busy";
        return false;
    }
    /* Claim the OTA state before handing the offer over, so the caller's idle
     * check and this claim are one step and no PTT can begin in between. The
     * signature work then happens on ota_task. */
    s_active = true;
    staged.source = *source;
    staged.session = session;
    staged.payload_len = (uint16_t)payload_len;
    memcpy(staged.payload, payload, payload_len);
    if (xQueueSend(s_queue, &staged, 0) != pdTRUE) {
        s_active = false;
        if (reject_reason) *reject_reason = "OTA queue busy";
        return false;
    }
    return true;
}

bool ota_manager_is_active(void) { return s_active; }
bool ota_manager_has_recent_error(void)
{
    return (int32_t)(s_error_until_ms - now_ms()) > 0;
}

void ota_manager_mark_running_valid(void)
{
    const esp_partition_t *running = esp_ota_get_running_partition();
    esp_ota_img_states_t state;
    if (running && esp_ota_get_state_partition(running, &state) == ESP_OK &&
        state == ESP_OTA_IMG_PENDING_VERIFY) {
        ESP_LOGI(TAG, "OTA health checks passed; committing %s", esp_app_get_description()->version);
        ESP_ERROR_CHECK(esp_ota_mark_app_valid_cancel_rollback());
    }
}
