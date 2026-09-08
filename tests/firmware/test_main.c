#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "protocol.h"
#include "session_policy.h"
/* Include the implementation to verify frozen historical NVS layouts as well
 * as public read/write paths, using only NVS/MAC/log substitutes. */
#include "device_config.c"
#include "usb_control.c"

static unsigned char blob[1024];
static size_t blob_size;
static bool fail_commit;
int nvs_open(const char *name, int mode, nvs_handle_t *h) { (void)name; (void)mode; *h = 1; return 0; }
int nvs_get_blob(nvs_handle_t h, const char *key, void *out, size_t *size)
{
    (void)h; (void)key;
    if (!blob_size) return ESP_ERR_NVS_NOT_FOUND;
    if (out) { assert(*size >= blob_size); memcpy(out, blob, blob_size); }
    *size = blob_size; return 0;
}
int nvs_set_blob(nvs_handle_t h, const char *key, const void *in, size_t size)
{ (void)h; (void)key; if (fail_commit) return 1; assert(size <= sizeof(blob)); memcpy(blob, in, size); blob_size = size; return 0; }
int nvs_commit(nvs_handle_t h) { (void)h; return 0; }
void nvs_close(nvs_handle_t h) { (void)h; }
const char *esp_err_to_name(int err) { (void)err; return "mock"; }
int esp_read_mac(uint8_t *mac, int type) { (void)type; const uint8_t id[] = {1,2,3,4,5,6}; memcpy(mac,id,6); return 0; }

static void config_tests(void)
{
    device_config_t config, rebooted;
    device_config_load(&config);
    assert(!config.assistant_enabled && !config.assistant_service_id);
    bool restart = true;
    assert(device_config_apply_json(&config, "{\"assistant_enabled\":false,\"assistant_service_id\":0}", &restart));
    assert(!restart);
    assert(device_config_apply_json(&config, "{\"alias\":\"Kitchen\",\"password\":\"secret\",\"ssid\":\"wifi\",\"speaker_volume\":640,\"led_brightness\":100,\"buttons_swapped\":true,\"ring_orientation\":180,\"soft_mute\":true}", &restart));
    assert(restart);
    device_config_t existing = config;
    assert(device_config_apply_json(&config, "{\"assistant_enabled\":true,\"assistant_service_id\":4294967295}", &restart));
    assert(!restart && config.assistant_enabled && config.assistant_service_id == UINT32_MAX);
    device_config_load(&rebooted);
    assert(memcmp(&config, &rebooted, sizeof(config)) == 0);
    assert(device_config_apply_json(&config, "{\"assistant_service_id\":42}", &restart));
    device_config_load(&rebooted); // no discovery/service present
    assert(rebooted.assistant_service_id == 42 && rebooted.assistant_enabled);
    assert(device_config_apply_json(&config, "{\"future_field\":true}", &restart));
    assert(config.assistant_enabled && config.assistant_service_id == 42);
    const char *invalid[] = {"{\"assistant_enabled\":1}", "{\"assistant_enabled\":null}", "{\"assistant_service_id\":-1}", "{\"assistant_service_id\":4294967296}", "{\"assistant_service_id\":1.5}", "{\"assistant_service_id\":\"42\"}", "{\"assistant_service_id\":null}"};
    for (unsigned i = 0; i < sizeof(invalid)/sizeof(invalid[0]); i++) {
        device_config_t before = config;
        assert(!device_config_apply_json(&config, invalid[i], &restart));
        assert(memcmp(&before, &config, sizeof(config)) == 0);
    }
    config.assistant_enabled = 0; config.assistant_service_id = 0;
    assert(memcmp(&config, &existing, sizeof(config)) == 0); // old fields untouched
    fail_commit = true;
    assert(!device_config_apply_json(&config, "{\"assistant_enabled\":true}", &restart));
    assert(!config.assistant_enabled);
    fail_commit = false;
    char json[768];
    assert(device_config_to_json(&rebooted, true, json, sizeof(json)) > 0);
    assert(strstr(json, "\"assistant_enabled\":true") && strstr(json, "\"assistant_service_id\":42"));
    assert(!strstr(json, "secret") && !strstr(json, "password"));
    assert(strstr(json, "\"hw_muted\":true") && strstr(json, "\"soft_mute\":true"));
    // Migration includes poisoned historical tail padding: it must never enable assistance.
    stored_config_v2_t v2;
    memset(&v2, 0xff, sizeof(v2)); v2.version = 2;
    memcpy(&v2.value, &existing, offsetof(device_config_v2_t, soft_mute) + 1);
    memcpy(blob, &v2, sizeof(v2)); blob_size = sizeof(v2);
    device_config_load(&config);
    assert(!config.assistant_enabled && !config.assistant_service_id);
    assert(memcmp(&config, &existing, sizeof(config)) == 0);
    device_config_load(&rebooted);
    assert(memcmp(&config, &rebooted, sizeof(config)) == 0);
    stored_config_v1_t v1 = {.version = 1};
    memcpy(&v1.value, &existing, sizeof(v1.value));
    memcpy(blob, &v1, sizeof(v1)); blob_size = sizeof(v1);
    device_config_load(&config);
    assert(!config.soft_mute && !config.assistant_enabled && !config.assistant_service_id);
    assert(config.speaker_volume == 640 && config.hardware_flags == 3 && !strcmp(config.wifi_password, "secret"));
    puts("PASS firmware JSON, NVS v1/v2/v3 migration, defaults, validation, persistence and password non-disclosure");
}

static void *config_writer(void *field)
{
    for (unsigned i = 1; i <= 30; i++) {
        char request[100], response[768]; bool restart = false;
        snprintf(request, sizeof(request), "{\"cmd\":\"set\",\"%s\":%u}", (char *)field,
                 !strcmp(field, "speaker_volume") ? 700u : i);
        assert(usb_control_request(request, response, sizeof(response), &restart));
        assert(!restart);
    }
    return NULL;
}
static void usb_tests(void)
{
    device_config_t config; device_config_load(&config);
    SemaphoreHandle_t lock = xSemaphoreCreateMutex();
    usb_control_start(&config, lock); // checks the actual TX ring fits config JSON
    char response[768]; bool restart = false;
    assert(usb_control_request("{\"cmd\":\"set\",\"assistant_enabled\":true}", response, sizeof(response), &restart));
    assert(strstr(response, "\"assistant_enabled\":true") && !strstr(response,"password"));
    pthread_t a, b;
    assert(!pthread_create(&a, NULL, config_writer, "assistant_service_id"));
    assert(!pthread_create(&b, NULL, config_writer, "speaker_volume"));
    pthread_join(a, NULL); pthread_join(b, NULL);
    assert(config.assistant_service_id == 30 && config.speaker_volume == 700 && config.assistant_enabled);
    device_config_t rebooted; device_config_load(&rebooted);
    assert(!memcmp(&config, &rebooted, sizeof(config)));
    assert(usb_control_request("{\"cmd\":\"get\"}", response, sizeof(response), &restart));
    assert(strstr(response, "\"assistant_service_id\":30"));
    pthread_mutex_destroy(lock); free(lock);
    pthread_mutex_destroy(s_write_lock); free(s_write_lock); s_write_lock = NULL;
    puts("PASS shared USB/remote config transactions, concurrent disjoint updates and USB reply capacity");
}

static void protocol_tests(void)
{
    uint8_t bytes[256]; intercom_pkt_t pkt;
    size_t n = protocol_pack(bytes, PKT_CLAIM, PROTO_FLAG_DIRECTED, 0x4d455348, 0x11223344, 0x55667788, 0, 0, NULL, 0);
    const uint8_t golden[32] = {'P','T','T','1',1,1,0,32,0x4d,0x45,0x53,0x48,0x11,0x22,0x33,0x44,0x55,0x66,0x77,0x88,0,0,0,0,0,0,0,0,0,0,0,3};
    assert(n == 32 && !memcmp(bytes, golden, 32));
    assert(protocol_parse(bytes,n,0x4d455348,1,&pkt) && pkt.session_id == 0x55667788);
    bytes[31] = 2; assert(!protocol_parse(bytes,n,0x4d455348,1,&pkt));
    bytes[4] = PKT_HELLO; assert(protocol_parse(bytes,n,0x4d455348,1,&pkt));
    uint8_t caps[] = {0,1,2,4,3,5,6,7,0x80,0xff};
    for (unsigned i = 0; i < sizeof(caps); i++) {
        n = protocol_hello_pack(bytes,sizeof(bytes),caps[i],7,"1.2.3","Kitchen");
        intercom_hello_t hello;
        assert(protocol_hello_parse(bytes,n,&hello));
        assert(hello.version == 3 && hello.capabilities == caps[i] && hello.flags == 7);
        assert(hello.alias_len == 7 && !memcmp(hello.alias,"Kitchen",7));
        assert(!protocol_hello_parse(bytes,7,&hello));
    }
    puts("PASS firmware PTT1 golden wire, revision rejection and HELLO capability roundtrips");
}

static void policy_tests(void)
{
    broadcast_floor_t b = {0}, d = {0};
    assert(broadcast_claim(&b,1,100,0));
    assert(!broadcast_claim(&b,2,200,1));
    assert(broadcast_claim(&b,2,50,2)); // deterministic collision arbitration
    assert(b.sender == 2);
    assert(directed_accept(false,true,false,2,50,3,300)); // preempt broadcast
    assert(b.sender == 2); // directed reservation never changes floor
    assert(!directed_accept(false,true,true,3,300,4,400)); // endpoint busy
    assert(directed_accept(false,true,true,3,300,3,300)); // repeated CLAIM
    assert(directed_accept(false,false,false,0,0,4,400)); // unrelated receiver free
    assert(!directed_accept(true,false,false,0,0,3,300)); // local TX priority
    assert(broadcast_claim(&d,5,500,100)); // broadcast coexists with those reservations
    broadcast_expire(&d,850); assert(d.active);
    broadcast_expire(&d,851); assert(!d.active);
    b = (broadcast_floor_t){1,100,UINT32_MAX-100,true};
    broadcast_expire(&b,649); assert(b.active);
    broadcast_expire(&b,650); assert(!b.active);
    puts("PASS firmware broadcast arbitration, independent reservations, priority and wrap-safe timeout policy");
}
int main(void) { protocol_tests(); policy_tests(); config_tests(); usb_tests(); return 0; }
