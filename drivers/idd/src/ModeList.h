// SPDX-License-Identifier: MIT
// Display modes reported by SplitPlayIdd.
//
// Moonlight asks for WxH@FPS; the service then applies exactly that mode through SP_IOCTL_IDD_SET_MODE.
// The list below is what the OS sees as monitor/target modes, so it must contain every mode a client may
// ask for, up to 500 Hz (ARCHITECTURE.md section 5: "IDD declares modes up to 500 Hz, vSyncFreq = 500/1").
// All modes are progressive: remote-session drivers cannot report interlaced modes.
//
// Kept free of Windows headers so that drivers/tests can exercise it on any compiler.
#pragma once

#include <stdint.h>

namespace splitplay::idd {

struct Mode {
  uint32_t width;
  uint32_t height;
  uint32_t refreshHz; // vSyncFreq = refreshHz / 1
};

struct Resolution {
  uint32_t width;
  uint32_t height;
};

constexpr Resolution kResolutions[] = {
  {3840, 2160}, {3440, 1440}, {2560, 1600}, {2560, 1440}, {1920, 1200},
  {1920, 1080}, {1680, 1050}, {1600, 900},  {1366, 768},  {1280, 800},
  {1280, 720},
};

// 250/500 match DWMFRAMEINTERVAL 4/2 ms (docs/contracts/config.md, host.composeIntervalMs).
constexpr uint32_t kRefreshRatesHz[] = {30, 60, 75, 90, 100, 120, 144, 165, 180, 200, 240, 250, 288, 300, 360, 480, 500};

constexpr uint32_t kResolutionCount = sizeof(kResolutions) / sizeof(kResolutions[0]);
constexpr uint32_t kRefreshRateCount = sizeof(kRefreshRatesHz) / sizeof(kRefreshRatesHz[0]);
constexpr uint32_t kModeCount = kResolutionCount * kRefreshRateCount;
constexpr uint32_t kMaxRefreshHz = 500;

// Default mode until the service applies the client's mode: 1920x1080@60.
constexpr Mode kDefaultMode = {1920, 1080, 60};

// Mode `index` in [0, kModeCount): resolutions in the order above, each with every refresh rate.
constexpr Mode ModeAt(uint32_t index) {
  return Mode{kResolutions[index / kRefreshRateCount].width,
              kResolutions[index / kRefreshRateCount].height,
              kRefreshRatesHz[index % kRefreshRateCount]};
}

// Index of `mode` in the list, or kModeCount if it is not listed.
constexpr uint32_t IndexOf(Mode mode) {
  for (uint32_t i = 0; i < kModeCount; ++i) {
    const Mode m = ModeAt(i);
    if (m.width == mode.width && m.height == mode.height && m.refreshHz == mode.refreshHz) {
      return i;
    }
  }
  return kModeCount;
}

constexpr uint32_t kPreferredModeIndex = IndexOf(kDefaultMode);

// True if width x height @ num/den is one of the reported modes (den must divide num exactly).
constexpr bool IsSupported(uint32_t width, uint32_t height, uint32_t refreshNum, uint32_t refreshDen) {
  if (refreshDen == 0 || refreshNum % refreshDen != 0) {
    return false;
  }
  return IndexOf(Mode{width, height, refreshNum / refreshDen}) != kModeCount;
}

static_assert(kPreferredModeIndex < kModeCount, "default mode must be listed");
static_assert(ModeAt(kModeCount - 1).refreshHz == kMaxRefreshHz, "500 Hz must be listed");

} // namespace splitplay::idd
