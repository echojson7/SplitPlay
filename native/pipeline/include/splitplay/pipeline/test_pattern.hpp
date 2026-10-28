// SPDX-License-Identifier: MIT
// Deterministic moving test pattern (BGRA8). Producer and verifier compute the same pixels from the frame id, so a
// frame can be checked end to end without sending the original.
#pragma once

#include <cstddef>
#include <cstdint>

namespace splitplay::pipeline {

inline constexpr uint32_t kPatternBarWidth = 32;

// One BGRA pixel as a little-endian uint32 (B in the low byte, A = 0xFF).
inline uint32_t pattern_pixel(uint64_t frame_id, uint32_t x, uint32_t y, uint32_t width, uint32_t height) noexcept {
  const uint32_t bar_x = static_cast<uint32_t>((frame_id * 8u) % (width ? width : 1u));
  if (x >= bar_x && x < bar_x + kPatternBarWidth) return 0xFFFFFFFFu;  // moving white bar
  const uint32_t b = static_cast<uint32_t>((frame_id * 37u) & 0xFFu);
  const uint32_t g = width ? (x * 255u) / width : 0u;
  const uint32_t r = height ? (y * 255u) / height : 0u;
  return 0xFF000000u | (r << 16) | (g << 8) | b;
}

// Fills `dst` (row pitch `pitch` bytes) with the pattern for `frame_id`.
void fill_pattern(uint64_t frame_id, uint32_t width, uint32_t height, uint8_t* dst, size_t pitch) noexcept;

// FNV-1a 64 over the visible bytes (width * 4 per row), ignoring row padding.
uint64_t hash_pixels(const uint8_t* pixels, uint32_t width, uint32_t height, size_t pitch) noexcept;

// Hash of the pattern for `frame_id`, i.e. hash_pixels(fill_pattern(...)).
uint64_t pattern_hash(uint64_t frame_id, uint32_t width, uint32_t height);

}  // namespace splitplay::pipeline
