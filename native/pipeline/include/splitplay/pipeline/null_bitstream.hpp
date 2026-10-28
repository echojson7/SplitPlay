// SPDX-License-Identifier: MIT
// "Bitstream" produced by NullEncoder: a header with the frame size and a hash of the source pixels, followed by a
// subsampled thumbnail. Lets the receiving end verify the whole capture -> encode -> transport path.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace splitplay::pipeline {

#pragma pack(push, 1)
struct NullBitstreamHeader {
  uint32_t magic;         // kNullBitstreamMagic
  uint16_t version;       // 1
  uint16_t flags;         // bit0 keyframe
  uint64_t frame_id;
  uint32_t width;
  uint32_t height;
  uint32_t thumb_width;
  uint32_t thumb_height;
  uint32_t step;          // thumbnail pixel (tx, ty) = source pixel (tx * step, ty * step)
  uint32_t reserved;
  uint64_t content_hash;  // hash_pixels() of the full source frame
};
#pragma pack(pop)
static_assert(sizeof(NullBitstreamHeader) == 48, "NullBitstreamHeader layout");

inline constexpr uint32_t kNullBitstreamMagic = 0x424E5053u;  // 'SPNB'

// Builds the bitstream from BGRA8 pixels.
std::vector<uint8_t> build_null_bitstream(uint64_t frame_id, uint32_t width, uint32_t height, const uint8_t* pixels,
                                          size_t pitch, bool keyframe, uint32_t step, uint64_t* content_hash);

struct NullBitstreamInfo {
  uint64_t frame_id = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint64_t content_hash = 0;
  bool keyframe = false;
};

// Parses and checks the structure. With `expect_test_pattern`, also checks the thumbnail and the content hash
// against the synthetic test pattern for the frame id.
bool verify_null_bitstream(const uint8_t* data, size_t size, bool expect_test_pattern, NullBitstreamInfo* info,
                           std::string* error);

}  // namespace splitplay::pipeline
