// SPDX-License-Identifier: MIT
#include "splitplay/pipeline/null_bitstream.hpp"

#include <cstring>
#include <format>

#include "splitplay/pipeline/test_pattern.hpp"

namespace splitplay::pipeline {

std::vector<uint8_t> build_null_bitstream(uint64_t frame_id, uint32_t width, uint32_t height, const uint8_t* pixels,
                                          size_t pitch, bool keyframe, uint32_t step, uint64_t* content_hash) {
  if (step == 0) step = 1;
  NullBitstreamHeader h{};
  h.magic = kNullBitstreamMagic;
  h.version = 1;
  h.flags = keyframe ? 1 : 0;
  h.frame_id = frame_id;
  h.width = width;
  h.height = height;
  h.thumb_width = (width + step - 1) / step;
  h.thumb_height = (height + step - 1) / step;
  h.step = step;
  h.content_hash = hash_pixels(pixels, width, height, pitch);
  if (content_hash) *content_hash = h.content_hash;

  std::vector<uint8_t> out(sizeof(h) + static_cast<size_t>(h.thumb_width) * h.thumb_height * 4u);
  std::memcpy(out.data(), &h, sizeof(h));
  uint8_t* dst = out.data() + sizeof(h);
  for (uint32_t ty = 0; ty < h.thumb_height; ++ty) {
    const uint8_t* row = pixels + static_cast<size_t>(ty) * step * pitch;
    for (uint32_t tx = 0; tx < h.thumb_width; ++tx) {
      std::memcpy(dst, row + static_cast<size_t>(tx) * step * 4u, 4);
      dst += 4;
    }
  }
  return out;
}

bool verify_null_bitstream(const uint8_t* data, size_t size, bool expect_test_pattern, NullBitstreamInfo* info,
                           std::string* error) {
  auto fail = [&](std::string msg) {
    if (error) *error = std::move(msg);
    return false;
  };
  if (size < sizeof(NullBitstreamHeader)) return fail("bitstream shorter than its header");
  NullBitstreamHeader h;
  std::memcpy(&h, data, sizeof(h));
  if (h.magic != kNullBitstreamMagic) return fail(std::format("bad bitstream magic 0x{:08x}", h.magic));
  if (h.version != 1) return fail(std::format("unsupported bitstream version {}", h.version));
  if (h.step == 0 || h.width == 0 || h.height == 0) return fail("empty frame in bitstream");
  if (h.thumb_width != (h.width + h.step - 1) / h.step || h.thumb_height != (h.height + h.step - 1) / h.step)
    return fail("inconsistent thumbnail size");
  const size_t expected = sizeof(h) + static_cast<size_t>(h.thumb_width) * h.thumb_height * 4u;
  if (size != expected) return fail(std::format("bitstream size {} != expected {}", size, expected));
  if (info) {
    info->frame_id = h.frame_id;
    info->width = h.width;
    info->height = h.height;
    info->content_hash = h.content_hash;
    info->keyframe = (h.flags & 1) != 0;
  }
  if (!expect_test_pattern) return true;

  const uint8_t* thumb = data + sizeof(h);
  for (uint32_t ty = 0; ty < h.thumb_height; ++ty) {
    for (uint32_t tx = 0; tx < h.thumb_width; ++tx) {
      uint32_t got;
      std::memcpy(&got, thumb + (static_cast<size_t>(ty) * h.thumb_width + tx) * 4u, 4);
      const uint32_t want = pattern_pixel(h.frame_id, tx * h.step, ty * h.step, h.width, h.height);
      if (got != want)
        return fail(std::format("frame {}: pixel ({}, {}) is 0x{:08x}, expected 0x{:08x}", h.frame_id, tx * h.step,
                                ty * h.step, got, want));
    }
  }
  const uint64_t want_hash = pattern_hash(h.frame_id, h.width, h.height);
  if (want_hash != h.content_hash)
    return fail(std::format("frame {}: content hash {:016x}, expected {:016x}", h.frame_id, h.content_hash, want_hash));
  return true;
}

}  // namespace splitplay::pipeline
