#include "ring_controller.h"

#include <string.h>
#include <stdlib.h>

#include "esp_check.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "driver/rmt_encoder.h"
#include "driver/rmt_tx.h"

#include "RingAnimations.h"

namespace {
constexpr uint32_t kResolutionHz = 10000000;
constexpr size_t kMaxPixels = 24;
static const char *TAG = "ring";

struct LedEncoder {
  rmt_encoder_t base;
  rmt_encoder_t *bytes = nullptr;
  rmt_encoder_t *copy = nullptr;
  int state = 0;
  rmt_symbol_word_t reset{};
};

size_t encode(rmt_encoder_t *encoder, rmt_channel_handle_t channel,
              const void *data, size_t size, rmt_encode_state_t *result) {
  auto *self = __containerof(encoder, LedEncoder, base);
  rmt_encode_state_t state = RMT_ENCODING_RESET;
  rmt_encode_state_t session = RMT_ENCODING_RESET;
  size_t encoded = 0;
  if (self->state == 0) {
    encoded += self->bytes->encode(self->bytes, channel, data, size, &session);
    if (session & RMT_ENCODING_COMPLETE) self->state = 1;
    if (session & RMT_ENCODING_MEM_FULL) { *result = RMT_ENCODING_MEM_FULL; return encoded; }
  }
  if (self->state == 1) {
    encoded += self->copy->encode(self->copy, channel, &self->reset, sizeof(self->reset), &session);
    if (session & RMT_ENCODING_COMPLETE) { self->state = 0; state |= RMT_ENCODING_COMPLETE; }
    if (session & RMT_ENCODING_MEM_FULL) state |= RMT_ENCODING_MEM_FULL;
  }
  *result = state;
  return encoded;
}

esp_err_t reset(rmt_encoder_t *encoder) {
  auto *self = __containerof(encoder, LedEncoder, base);
  ESP_RETURN_ON_ERROR(rmt_encoder_reset(self->bytes), TAG, "reset bytes");
  ESP_RETURN_ON_ERROR(rmt_encoder_reset(self->copy), TAG, "reset copy");
  self->state = 0;
  return ESP_OK;
}

esp_err_t destroy(rmt_encoder_t *encoder) {
  auto *self = __containerof(encoder, LedEncoder, base);
  if (self->bytes) rmt_del_encoder(self->bytes);
  if (self->copy) rmt_del_encoder(self->copy);
  free(self);
  return ESP_OK;
}

esp_err_t make_encoder(rmt_encoder_handle_t *out) {
  auto *encoder = static_cast<LedEncoder *>(calloc(1, sizeof(LedEncoder)));
  if (!encoder) return ESP_ERR_NO_MEM;
  encoder->base.encode = encode;
  encoder->base.reset = reset;
  encoder->base.del = destroy;
  rmt_bytes_encoder_config_t bytes{};
  bytes.bit0 = {.level0 = 1, .duration0 = 3, .level1 = 0, .duration1 = 9};
  bytes.bit1 = {.level0 = 1, .duration0 = 9, .level1 = 0, .duration1 = 3};
  bytes.flags.msb_first = 1;
  esp_err_t err = rmt_new_bytes_encoder(&bytes, &encoder->bytes);
  if (err == ESP_OK) {
    rmt_copy_encoder_config_t copy{};
    err = rmt_new_copy_encoder(&copy, &encoder->copy);
  }
  if (err != ESP_OK) { destroy(&encoder->base); return err; }
  encoder->reset = {.level0 = 0, .duration0 = 250, .level1 = 0, .duration1 = 250};
  *out = &encoder->base;
  return ESP_OK;
}

class RmtRing final : public voicering::RingOutput {
 public:
  uint8_t size() const override { return count_; }
  void setPixel(uint8_t index, voicering::Rgb colour) override {
    if (index >= count_) return;
    pixels_[index * 3] = colour.g;
    pixels_[index * 3 + 1] = colour.r;
    pixels_[index * 3 + 2] = colour.b;
  }
  void show() override {
    rmt_transmit_config_t tx{};
    ESP_ERROR_CHECK(rmt_transmit(channel_, encoder_, pixels_, count_ * 3, &tx));
    ESP_ERROR_CHECK(rmt_tx_wait_all_done(channel_, 20));
  }
  void begin(uint8_t gpio, uint8_t count) {
    count_ = count > kMaxPixels ? kMaxPixels : count;
    rmt_tx_channel_config_t channel{};
    channel.clk_src = RMT_CLK_SRC_DEFAULT;
    channel.gpio_num = static_cast<gpio_num_t>(gpio);
    channel.mem_block_symbols = 64;
    channel.resolution_hz = kResolutionHz;
    channel.trans_queue_depth = 1;
    ESP_ERROR_CHECK(rmt_new_tx_channel(&channel, &channel_));
    ESP_ERROR_CHECK(make_encoder(&encoder_));
    ESP_ERROR_CHECK(rmt_enable(channel_));
    memset(pixels_, 0, sizeof(pixels_));
  }
 private:
  rmt_channel_handle_t channel_ = nullptr;
  rmt_encoder_handle_t encoder_ = nullptr;
  uint8_t count_ = kMaxPixels;
  uint8_t pixels_[kMaxPixels * 3]{};
};

RmtRing ring;
voicering::AnimationEngine *animation = nullptr;
ring_mode_t current = RING_IDLE;
}  // namespace

extern "C" void ring_controller_init(uint8_t gpio, uint8_t count, uint8_t brightness) {
  ring.begin(gpio, count);
  animation = new voicering::AnimationEngine(ring);
  animation->setGlobalBrightness(brightness);
  animation->startStartup(0);
}

extern "C" void ring_controller_set(ring_mode_t mode) {
  if (!animation || mode == current) return;
  current = mode;
  const uint32_t now = static_cast<uint32_t>(esp_timer_get_time() / 1000);
  switch (mode) {
    case RING_TALK_BROADCAST: animation->startTalk(voicering::TalkColour::GrassGreen, 0, now); break;
    case RING_TALK_REPLY: animation->startTalk(voicering::TalkColour::Blue, 0, now); break;
    case RING_SPEAKING: animation->startSpeaking(0, now); break;
    case RING_MUTE: animation->startMute(0, now); break;
    case RING_ERROR: animation->startError(now); break;
    default: animation->startIdle(now); break;
  }
}

extern "C" void ring_controller_update(uint32_t now) {
  if (animation) animation->update(now);
}
