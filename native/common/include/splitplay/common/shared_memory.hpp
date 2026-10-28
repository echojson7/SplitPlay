// SPDX-License-Identifier: MIT
// Named shared-memory sections and events for the capture ring and the input ring (Windows).
#pragma once

#ifdef _WIN32

#include <cstddef>
#include <memory>
#include <string>

#include "splitplay/capture_ring.h"
#include "splitplay/common/capture_ring.hpp"
#include "splitplay/common/object_names.hpp"
#include "splitplay/input_ring.h"
#include "splitplay/common/win_util.hpp"

namespace splitplay::shm {

// Security of objects we create: Global objects are restricted to SYSTEM and Administrators; Local objects
// (tests, self-test) use the default DACL of the creating process.
enum class ObjectSecurity { system_and_admins, process_default };
ObjectSecurity default_security(ObjectNamespace ns) noexcept;

class SharedSection {
 public:
  // Creates (or reuses, if it already exists) a pagefile-backed section of `size` bytes and maps it.
  static std::unique_ptr<SharedSection> create(const std::string& name, size_t size, ObjectSecurity security,
                                               std::string* error);
  // Opens an existing section and maps it read/write.
  static std::unique_ptr<SharedSection> open(const std::string& name, std::string* error);
  ~SharedSection();

  void* data() const noexcept { return view_; }
  size_t size() const noexcept { return size_; }
  bool existed() const noexcept { return existed_; }
  const std::string& name() const noexcept { return name_; }

 private:
  SharedSection() = default;
  win::UniqueHandle mapping_;
  void* view_ = nullptr;
  size_t size_ = 0;
  bool existed_ = false;
  std::string name_;
};

class NamedEvent {
 public:
  static std::unique_ptr<NamedEvent> create(const std::string& name, bool auto_reset, ObjectSecurity security,
                                            std::string* error);
  static std::unique_ptr<NamedEvent> open(const std::string& name, std::string* error);

  bool set() noexcept { return SetEvent(handle_.get()) != FALSE; }
  // WAIT_OBJECT_0 when signalled, WAIT_TIMEOUT otherwise.
  DWORD wait(DWORD timeout_ms) noexcept { return WaitForSingleObject(handle_.get(), timeout_ms); }
  HANDLE handle() const noexcept { return handle_.get(); }

 private:
  win::UniqueHandle handle_;
};

// Capture ring: created by the producer (the IDD; in Stage 2 the synthetic producer), opened by the worker.
struct CaptureRingMapping {
  std::unique_ptr<SharedSection> section;
  std::unique_ptr<NamedEvent> frame_ready;
  SpCaptureRingHeader* header() const noexcept { return static_cast<SpCaptureRingHeader*>(section->data()); }

  static std::unique_ptr<CaptureRingMapping> create(ObjectNamespace ns, const std::string& desk,
                                                    const capture::RingFormat& format, std::string* error);
  static std::unique_ptr<CaptureRingMapping> open(ObjectNamespace ns, const std::string& desk, std::string* error);
};

// Input ring: created by the seat (consumer), opened by the worker (producer).
struct InputRingMapping {
  std::unique_ptr<SharedSection> section;
  std::unique_ptr<NamedEvent> input_ready;
  SpInputRing* ring() const noexcept { return static_cast<SpInputRing*>(section->data()); }

  static std::unique_ptr<InputRingMapping> create(ObjectNamespace ns, const std::string& desk, uint32_t flags,
                                                  std::string* error);
  static std::unique_ptr<InputRingMapping> open(ObjectNamespace ns, const std::string& desk, std::string* error);
};

}  // namespace splitplay::shm

#endif  // _WIN32
