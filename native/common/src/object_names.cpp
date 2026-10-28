// SPDX-License-Identifier: MIT
#include "splitplay/common/object_names.hpp"

#include <format>

namespace splitplay {

std::string_view to_string(ObjectNamespace ns) noexcept { return ns == ObjectNamespace::global ? "global" : "local"; }

bool parse_object_namespace(std::string_view text, ObjectNamespace& out) noexcept {
  if (text == "global") {
    out = ObjectNamespace::global;
    return true;
  }
  if (text == "local") {
    out = ObjectNamespace::local;
    return true;
  }
  return false;
}

bool is_valid_desk_id(std::string_view desk) noexcept {
  if (desk.empty() || desk.size() > 32) return false;
  for (char c : desk) {
    const bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
    if (!ok) return false;
  }
  return true;
}

static std::string_view prefix(ObjectNamespace ns) { return ns == ObjectNamespace::global ? "Global\\" : "Local\\"; }

std::string kernel_object_name(ObjectNamespace ns, std::string_view kind, std::string_view desk) {
  return std::format("{}SplitPlay.{}.{}", prefix(ns), kind, desk);
}

std::string capture_ring_section_name(ObjectNamespace ns, std::string_view desk) {
  return kernel_object_name(ns, "CaptureRing", desk);
}

std::string frame_ready_event_name(ObjectNamespace ns, std::string_view desk) {
  return kernel_object_name(ns, "FrameReady", desk);
}

std::string frame_texture_name(ObjectNamespace ns, std::string_view desk, uint32_t generation, uint32_t slot) {
  return std::format("{}SplitPlay.Frame.{}.{}.{}", prefix(ns), desk, generation, slot);
}

std::string frame_fence_name(ObjectNamespace ns, std::string_view desk, uint32_t generation) {
  return std::format("{}SplitPlay.Fence.{}.{}", prefix(ns), desk, generation);
}

std::string input_ring_section_name(ObjectNamespace ns, std::string_view desk) {
  return kernel_object_name(ns, "InputRing", desk);
}

std::string input_ready_event_name(ObjectNamespace ns, std::string_view desk) {
  return kernel_object_name(ns, "InputReady", desk);
}

std::string control_pipe_name(std::string_view role, std::string_view desk) {
  return std::format("\\\\.\\pipe\\splitplay\\{}-{}", role, desk);
}

}  // namespace splitplay
