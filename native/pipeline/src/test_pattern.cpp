// SPDX-License-Identifier: MIT
#include "splitplay/pipeline/test_pattern.hpp"

#include <cstring>
#include <vector>

namespace splitplay::pipeline {

void fill_pattern(uint64_t frame_id, uint32_t width, uint32_t height, uint8_t* dst, size_t pitch) noexcept {
  for (uint32_t y = 0; y < height; ++y) {
    auto* row = reinterpret_cast<uint32_t*>(dst + static_cast<size_t>(y) * pitch);
    for (uint32_t x = 0; x < width; ++x) row[x] = pattern_pixel(frame_id, x, y, width, height);
  }
}

uint64_t hash_pixels(const uint8_t* pixels, uint32_t width, uint32_t height, size_t pitch) noexcept {
  uint64_t h = 1469598103934665603ull;
  const size_t row_bytes = static_cast<size_t>(width) * 4u;
  for (uint32_t y = 0; y < height; ++y) {
    const uint8_t* row = pixels + static_cast<size_t>(y) * pitch;
    for (size_t i = 0; i < row_bytes; ++i) {
      h ^= row[i];
      h *= 1099511628211ull;
    }
  }
  return h;
}

uint64_t pattern_hash(uint64_t frame_id, uint32_t width, uint32_t height) {
  std::vector<uint8_t> buf(static_cast<size_t>(width) * height * 4u);
  fill_pattern(frame_id, width, height, buf.data(), static_cast<size_t>(width) * 4u);
  return hash_pixels(buf.data(), width, height, static_cast<size_t>(width) * 4u);
}

}  // namespace splitplay::pipeline
