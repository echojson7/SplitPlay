// SPDX-License-Identifier: MIT
// Names of kernel objects and pipes (docs/contracts/README.md "Naming and ids", capture_ring.h, input_ring.h).
#pragma once

#include <cstdint>
#include <string>
#include <string_view>

namespace splitplay {

// Kernel objects live in the Global namespace in production (`Global\SplitPlay.<kind>.<desk-id>`).
// Tests and standalone self-tests use the session-local namespace, which needs no SeCreateGlobalPrivilege.
enum class ObjectNamespace { global, local };

std::string_view to_string(ObjectNamespace ns) noexcept;
bool parse_object_namespace(std::string_view text, ObjectNamespace& out) noexcept;

// Desk id: lowercase [a-z0-9-]{1,32}.
bool is_valid_desk_id(std::string_view desk) noexcept;

// `<ns>\SplitPlay.<kind>.<desk>`
std::string kernel_object_name(ObjectNamespace ns, std::string_view kind, std::string_view desk);

std::string capture_ring_section_name(ObjectNamespace ns, std::string_view desk);  // SplitPlay.CaptureRing.<desk>
std::string frame_ready_event_name(ObjectNamespace ns, std::string_view desk);     // SplitPlay.FrameReady.<desk>
std::string frame_texture_name(ObjectNamespace ns, std::string_view desk, uint32_t generation, uint32_t slot);
std::string frame_fence_name(ObjectNamespace ns, std::string_view desk, uint32_t generation);
std::string input_ring_section_name(ObjectNamespace ns, std::string_view desk);   // SplitPlay.InputRing.<desk>
std::string input_ready_event_name(ObjectNamespace ns, std::string_view desk);    // SplitPlay.InputReady.<desk>

// `\\.\pipe\splitplay\<role>-<desk>`
std::string control_pipe_name(std::string_view role, std::string_view desk);

}  // namespace splitplay
