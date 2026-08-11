#pragma once

/*
  Platform-independent renderer for the 24 pixel voice-status ring.

  This file deliberately has no Arduino, FastLED, or Adafruit NeoPixel
  dependency.  It only needs a RingOutput implementation, so it can move to
  ESP32-C3 unchanged.
*/

#include <stdint.h>

namespace voicering {

struct Rgb {
  uint8_t r;
  uint8_t g;
  uint8_t b;
};

class RingOutput {
 public:
  virtual ~RingOutput() = default;
  virtual uint8_t size() const = 0;
  virtual void setPixel(uint8_t index, Rgb color) = 0;
  virtual void show() = 0;
};

enum class TalkColour : uint8_t {
  GrassGreen,
  Blue,
};

class AnimationEngine {
 public:
  explicit AnimationEngine(RingOutput& ring, uint8_t visualCentreLed = 0)
      : ring_(ring), centre_(visualCentreLed % ring.size()) {}

  // A platform-independent master brightness. 255 is full output; 0 is off.
  // It scales every animation, including startup and transition fades.
  void setGlobalBrightness(uint8_t brightness) { globalBrightness_ = brightness; }
  uint8_t globalBrightness() const { return globalBrightness_; }

  // Convert a colour-wheel hue (0..359 degrees) to a saturated RGB colour.
  // This provides a convenient hue-based API without tying the engine to a
  // particular LED library's HSV implementation.
  static Rgb hue(uint16_t degrees) {
    degrees %= 360;
    const uint8_t sector = degrees / 60;
    const uint8_t offset = static_cast<uint8_t>(((degrees % 60) * 255UL) / 60);
    switch (sector) {
      case 0: return {255, offset, 0};
      case 1: return {static_cast<uint8_t>(255 - offset), 255, 0};
      case 2: return {0, 255, offset};
      case 3: return {0, static_cast<uint8_t>(255 - offset), 255};
      case 4: return {offset, 0, 255};
      default: return {255, 0, static_cast<uint8_t>(255 - offset)};
    }
  }

  // Non-blocking entry points intended for future external events.
  void startStartup(uint32_t now) { startStartup(defaultStartupColour(), now); }
  void startStartup(Rgb colour, uint32_t now) {
    startupColour_ = colour;
    start(Mode::Startup, now, 0);
  }
  void startIdle(uint32_t now) { start(Mode::Idle, now, 0); }

  // Use rotations == 0 to continue until stopTalk() is called.
  void startTalk(TalkColour colour, uint8_t rotations, uint32_t now) {
    startTalk(colour == TalkColour::GrassGreen ? defaultGrassGreen() : defaultBlue(),
              rotations, now);
  }
  void startTalk(Rgb colour, uint8_t rotations, uint32_t now) {
    talkColour_ = colour;
    start(Mode::Talk, now, rotations);
  }
  void stopTalk(uint32_t now) { beginFadeOut(now); }

  // Use cycles == 0 to continue until the corresponding stop call.
  void startMute(uint8_t cycles, uint32_t now) {
    startMute(defaultMuteColour(), cycles, now);
  }
  void startMute(Rgb colour, uint8_t cycles, uint32_t now) {
    muteColour_ = colour;
    start(Mode::Mute, now, cycles);
  }
  void stopMute(uint32_t now) { beginFadeOut(now); }
  void startSpeaking(uint8_t cycles, uint32_t now) {
    startSpeaking(defaultSpeakingColour(), cycles, now);
  }
  void startSpeaking(Rgb colour, uint8_t cycles, uint32_t now) {
    speakingColour_ = colour;
    start(Mode::Speaking, now, cycles);
  }
  void stopSpeaking(uint32_t now) { beginFadeOut(now); }

  // Two short red fade-in/fade-out pulses for a rejected PTT attempt.
  void startError(uint32_t now) { start(Mode::Error, now, 0); }

  // Clearly named aliases for the current single-button demonstration UI.
  void startMutePreview(uint8_t cycles, uint32_t now) { startMute(cycles, now); }
  void startSpeakingPreview(uint8_t cycles, uint32_t now) {
    startSpeaking(cycles, now);
  }

  bool isIdle() const { return mode_ == Mode::Idle; }

  void update(uint32_t now) {
    const uint32_t elapsed = now - stateStartedAt_;

    switch (mode_) {
      case Mode::Startup:
        renderStartup(elapsed);
        if (elapsed >= kStartupMs) startIdle(now);
        break;
      case Mode::Idle:
        clear();
        break;
      case Mode::Talk:
        updateTalk(now, elapsed);
        break;
      case Mode::Mute:
        updateBreathing(now, elapsed, muteColour_, false);
        break;
      case Mode::Speaking:
        updateBreathing(now, elapsed, speakingColour_, true);
        break;
      case Mode::Error:
        updateError(now, elapsed);
        break;
      case Mode::FadeOut:
        updateFadeOut(now, elapsed);
        break;
    }

    ring_.show();
  }

 private:
  enum class Mode : uint8_t { Startup, Idle, Talk, Mute, Speaking, Error, FadeOut };

  static constexpr uint16_t kFrameIntervalMs = 20;
  static constexpr uint16_t kStartupMs = 2000;
  static constexpr uint16_t kFadeMs = 700;
  static constexpr uint16_t kShadowFadeInMs = 700;
  static constexpr uint16_t kRotationMs = 5000;
  static constexpr uint16_t kBreathMs = 2000;
  static constexpr uint16_t kErrorFadeMs = 180;
  static constexpr uint8_t kMuteBreathPeak = 72;  // About 28% brightness.

  RingOutput& ring_;
  uint8_t centre_;
  Mode mode_ = Mode::Idle;
  uint8_t globalBrightness_ = 255;
  Rgb startupColour_ = {255, 241, 185};
  Rgb talkColour_ = {124, 252, 0};
  Rgb muteColour_ = {255, 0, 0};
  Rgb speakingColour_ = {255, 88, 0};
  uint8_t limit_ = 0;  // 0 means unbounded rotations/cycles.
  uint32_t stateStartedAt_ = 0;
  uint32_t lastFrameAt_ = 0;
  Rgb fadeOutColour_ = {0, 0, 0};
  uint8_t fadeOutStartBrightness_ = 0;
  uint8_t lastRenderedBrightness_ = 0;

  void start(Mode newMode, uint32_t now, uint8_t limit) {
    mode_ = newMode;
    limit_ = limit;
    stateStartedAt_ = now;
    lastFrameAt_ = now - kFrameIntervalMs;
  }

  void beginFadeOut(uint32_t now) {
    if (mode_ == Mode::Idle || mode_ == Mode::FadeOut) return;
    fadeOutColour_ = currentBaseColour();
    fadeOutStartBrightness_ = lastRenderedBrightness_;
    start(Mode::FadeOut, now, 0);
  }

  bool shouldRender(uint32_t now) {
    if (now - lastFrameAt_ < kFrameIntervalMs) return false;
    lastFrameAt_ = now;
    return true;
  }

  void updateTalk(uint32_t now, uint32_t elapsed) {
    if (!shouldRender(now)) return;

    const uint32_t fadeAndRotation =
        kFadeMs + kShadowFadeInMs + uint32_t(limit_) * kRotationMs;
    if (limit_ != 0 && elapsed >= fadeAndRotation + kFadeMs) {
      startIdle(now);
      return;
    }

    if (elapsed < kFadeMs) {
      renderSolid(currentBaseColour(), linear(elapsed, kFadeMs));
      return;
    }

    // Let the shadow fade in while stationary before its first rotation. This
    // prevents a dark gap from appearing abruptly in a fully lit ring.
    if (elapsed < kFadeMs + kShadowFadeInMs) {
      renderTalkTracer(0, 255, linear(elapsed - kFadeMs, kShadowFadeInMs));
      return;
    }

    if (limit_ != 0 && elapsed >= fadeAndRotation) {
      const uint32_t fadeElapsed = elapsed - fadeAndRotation;
      renderTalkTracer(kRotationMs - 1, 255 - linear(fadeElapsed, kFadeMs));
      return;
    }

    renderTalkTracer(elapsed - kFadeMs - kShadowFadeInMs, 255);
  }

  void updateBreathing(uint32_t now, uint32_t elapsed, Rgb colour,
                       bool fullBrightnessBreath) {
    if (!shouldRender(now)) return;

    // The mute animation fades gently down from its bright entrance to the
    // quieter breathing ceiling. Orange breathing already begins at 100%, so
    // it does not need this extra transition.
    const uint32_t breathingTransitionMs = fullBrightnessBreath ? 0 : kFadeMs;
    const uint32_t activeStartedAt = kFadeMs + breathingTransitionMs;
    const uint32_t activeDuration = uint32_t(limit_) * kBreathMs;
    if (limit_ != 0 && elapsed >= activeStartedAt + activeDuration + kFadeMs) {
      startIdle(now);
      return;
    }

    if (elapsed < kFadeMs) {
      renderSolid(colour, linear(elapsed, kFadeMs));
      return;
    }

    if (elapsed < activeStartedAt) {
      const uint32_t transitionElapsed = elapsed - kFadeMs;
      const uint8_t transition = linear(transitionElapsed, kFadeMs);
      const uint8_t brightness =
          255 - scale8(255 - kMuteBreathPeak, transition);
      renderSolid(colour, brightness);
      return;
    }

    if (limit_ != 0 && elapsed >= activeStartedAt + activeDuration) {
      const uint32_t fadeElapsed = elapsed - activeStartedAt - activeDuration;
      renderSolid(colour, 255 - linear(fadeElapsed, kFadeMs));
      return;
    }

    const uint32_t breathElapsed = elapsed - activeStartedAt;
    const uint8_t brightness = breathingBrightness(breathElapsed, fullBrightnessBreath);
    renderSolid(colour, brightness);
  }

  void updateFadeOut(uint32_t now, uint32_t elapsed) {
    if (!shouldRender(now)) return;
    if (elapsed >= kFadeMs) {
      startIdle(now);
      return;
    }
    const uint8_t brightness = scale8(fadeOutStartBrightness_,
                                      255 - linear(elapsed, kFadeMs));
    renderSolid(fadeOutColour_, brightness);
  }

  void updateError(uint32_t now, uint32_t elapsed) {
    if (!shouldRender(now)) return;
    const uint32_t pulseMs = uint32_t(kErrorFadeMs) * 2;
    if (elapsed >= pulseMs * 2) {
      startIdle(now);
      return;
    }
    const uint32_t inPulse = elapsed % pulseMs;
    const uint8_t brightness = inPulse < kErrorFadeMs
        ? linear(inPulse, kErrorFadeMs)
        : 255 - linear(inPulse - kErrorFadeMs, kErrorFadeMs);
    renderSolid(defaultMuteColour(), brightness);
  }

  void renderStartup(uint32_t elapsed) {
    if (!shouldRender(elapsed + stateStartedAt_)) return;
    clear();

    // Each of 13 fronts takes an equal slice of the two-second startup.
    const uint8_t completedSteps = (elapsed >= kStartupMs)
        ? 12
        : static_cast<uint8_t>((uint32_t(elapsed) * 13) / kStartupMs);
    for (uint8_t distance = 0; distance <= completedSteps; ++distance) {
      setRelative(distance, scale(startupColour_, globalBrightness_));
      setRelative(-static_cast<int8_t>(distance),
                  scale(startupColour_, globalBrightness_));
    }
  }

  void renderTalkTracer(uint32_t rotatingElapsed, uint8_t overallBrightness,
                        uint8_t shadowStrength = 255) {
    const Rgb colour = currentBaseColour();
    lastRenderedBrightness_ = overallBrightness;

    // Keep the shadow position in 1/256 LED units. This gives a continuous
    // fade from one LED to the next instead of a visible 208 ms jump.
    const int32_t ringSizeQ8 = int32_t(ring_.size()) * 256;
    const int32_t shadowPositionQ8 = int32_t(centre_) * 256 +
        int32_t((uint32_t(rotatingElapsed % kRotationMs) * ring_.size() * 256UL) /
                kRotationMs);
    constexpr int32_t kShadowRadiusQ8 = 896;  // 3.5 LEDs: seven LEDs affected.

    for (uint8_t i = 0; i < ring_.size(); ++i) {
      int32_t distanceQ8 = int32_t(i) * 256 - shadowPositionQ8;
      while (distanceQ8 > ringSizeQ8 / 2) distanceQ8 -= ringSizeQ8;
      while (distanceQ8 < -ringSizeQ8 / 2) distanceQ8 += ringSizeQ8;
      if (distanceQ8 < 0) distanceQ8 = -distanceQ8;

      uint8_t brightness = overallBrightness;
      if (distanceQ8 < kShadowRadiusQ8) {
        // Cubic falloff: the centre and nearby LEDs are substantially darker,
        // while the outer edge still blends gradually into the lit ring.
        const uint8_t linearDistance = static_cast<uint8_t>(
            (distanceQ8 * 255L) / kShadowRadiusQ8);
        const uint8_t square = scale8(linearDistance, linearDistance);
        const uint8_t shadowFactor = scale8(square, linearDistance);
        const uint8_t blendedShadowFactor =
            255 - scale8(255 - shadowFactor, shadowStrength);
        brightness = scale8(overallBrightness, blendedShadowFactor);
      }
      ring_.setPixel(i, scale(colour, scale8(brightness, globalBrightness_)));
    }
  }

  void renderSolid(Rgb colour, uint8_t brightness) {
    lastRenderedBrightness_ = brightness;
    const Rgb scaled = scale(colour, scale8(brightness, globalBrightness_));
    for (uint8_t i = 0; i < ring_.size(); ++i) ring_.setPixel(i, scaled);
  }

  void clear() { renderSolid({0, 0, 0}, 0); }

  void setRelative(int8_t relativeIndex, Rgb colour) {
    int16_t index = int16_t(centre_) + relativeIndex;
    const int16_t count = ring_.size();
    while (index < 0) index += count;
    while (index >= count) index -= count;
    ring_.setPixel(static_cast<uint8_t>(index), colour);
  }

  Rgb currentBaseColour() const {
    switch (mode_) {
      case Mode::Talk:
        return talkColour_;
      case Mode::Mute:
        return muteColour_;
      case Mode::Speaking:
        return speakingColour_;
      case Mode::Startup:
        return startupColour_;
      default:
        return fadeOutColour_;
    }
  }

  static uint8_t linear(uint32_t elapsed, uint16_t duration) {
    if (elapsed >= duration) return 255;
    return static_cast<uint8_t>((elapsed * 255UL) / duration);
  }

  static uint8_t scale8(uint8_t value, uint8_t scale) {
    return static_cast<uint8_t>((uint16_t(value) * scale + 127) / 255);
  }

  static Rgb scale(Rgb colour, uint8_t brightness) {
    return {scale8(colour.r, brightness), scale8(colour.g, brightness),
            scale8(colour.b, brightness)};
  }

  static Rgb defaultStartupColour() { return {255, 241, 185}; }
  static Rgb defaultGrassGreen() { return {124, 252, 0}; }
  static Rgb defaultBlue() { return {0, 132, 255}; }
  static Rgb defaultMuteColour() { return {255, 0, 0}; }
  static Rgb defaultSpeakingColour() { return {255, 88, 0}; }

  static uint8_t breathingBrightness(uint32_t elapsed, bool fullBrightness) {
    // Triangle wave: smooth enough at a 50 fps update rate, small and portable.
    // Offset it so a breath starts and ends at its maximum. This makes the
    // bright fade-in and subsequent fade-out continuous rather than abrupt.
    const uint16_t phase = static_cast<uint16_t>(
        ((elapsed % kBreathMs) * 512UL / kBreathMs + 256) % 512);
    const uint8_t triangle = phase < 256 ? phase : 511 - phase;  // 0..255..0
    return fullBrightness ? triangle : scale8(triangle, kMuteBreathPeak);
  }
};

}  // namespace voicering
