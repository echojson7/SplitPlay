// SPDX-License-Identifier: MIT
#include "splitplay/common/shared_memory.hpp"

#include <sddl.h>

#include "splitplay/common/input_ring.hpp"
#include "splitplay/common/log.hpp"

namespace splitplay::shm {

namespace {

// Owns a SECURITY_ATTRIBUTES built from SDDL (or none for the process default).
class SecurityAttributes {
 public:
  explicit SecurityAttributes(ObjectSecurity security) {
    if (security != ObjectSecurity::system_and_admins) return;
    // Protected DACL: full access for LocalSystem and the Administrators group only.
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)", SDDL_REVISION_1, &sd_,
                                                             nullptr)) {
      sa_.nLength = sizeof(sa_);
      sa_.lpSecurityDescriptor = sd_;
      sa_.bInheritHandle = FALSE;
      valid_ = true;
    }
  }
  ~SecurityAttributes() {
    if (sd_) LocalFree(sd_);
  }
  SECURITY_ATTRIBUTES* get() noexcept { return valid_ ? &sa_ : nullptr; }

 private:
  PSECURITY_DESCRIPTOR sd_ = nullptr;
  SECURITY_ATTRIBUTES sa_{};
  bool valid_ = false;
};

}  // namespace

ObjectSecurity default_security(ObjectNamespace ns) noexcept {
  return ns == ObjectNamespace::global ? ObjectSecurity::system_and_admins : ObjectSecurity::process_default;
}

std::unique_ptr<SharedSection> SharedSection::create(const std::string& name, size_t size, ObjectSecurity security,
                                                     std::string* error) {
  SecurityAttributes sa(security);
  const std::wstring wname = win::widen(name);
  const auto size64 = static_cast<unsigned long long>(size);
  HANDLE h = CreateFileMappingW(INVALID_HANDLE_VALUE, sa.get(), PAGE_READWRITE, static_cast<DWORD>(size64 >> 32),
                                static_cast<DWORD>(size64 & 0xffffffffu), wname.c_str());
  const DWORD err = GetLastError();
  if (h == nullptr) {
    if (error) *error = "CreateFileMapping " + name + " failed: " + win::error_message(err);
    return nullptr;
  }
  std::unique_ptr<SharedSection> s(new SharedSection());
  s->mapping_.reset(h);
  s->existed_ = err == ERROR_ALREADY_EXISTS;
  s->name_ = name;
  s->view_ = MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, size);
  if (s->view_ == nullptr) {
    if (error) *error = "MapViewOfFile " + name + " failed: " + win::last_error_message();
    return nullptr;
  }
  s->size_ = size;
  return s;
}

std::unique_ptr<SharedSection> SharedSection::open(const std::string& name, std::string* error) {
  const std::wstring wname = win::widen(name);
  HANDLE h = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, wname.c_str());
  if (h == nullptr) {
    if (error) *error = "OpenFileMapping " + name + " failed: " + win::last_error_message();
    return nullptr;
  }
  std::unique_ptr<SharedSection> s(new SharedSection());
  s->mapping_.reset(h);
  s->existed_ = true;
  s->name_ = name;
  s->view_ = MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0);
  if (s->view_ == nullptr) {
    if (error) *error = "MapViewOfFile " + name + " failed: " + win::last_error_message();
    return nullptr;
  }
  MEMORY_BASIC_INFORMATION mbi{};
  if (VirtualQuery(s->view_, &mbi, sizeof(mbi)) == 0) {
    if (error) *error = "VirtualQuery failed: " + win::last_error_message();
    return nullptr;
  }
  s->size_ = mbi.RegionSize;  // rounded up to the page size
  return s;
}

SharedSection::~SharedSection() {
  if (view_) UnmapViewOfFile(view_);
}

std::unique_ptr<NamedEvent> NamedEvent::create(const std::string& name, bool auto_reset, ObjectSecurity security,
                                               std::string* error) {
  SecurityAttributes sa(security);
  const std::wstring wname = win::widen(name);
  HANDLE h = CreateEventW(sa.get(), auto_reset ? FALSE : TRUE, FALSE, wname.c_str());
  if (h == nullptr) {
    if (error) *error = "CreateEvent " + name + " failed: " + win::last_error_message();
    return nullptr;
  }
  auto e = std::make_unique<NamedEvent>();
  e->handle_.reset(h);
  return e;
}

std::unique_ptr<NamedEvent> NamedEvent::open(const std::string& name, std::string* error) {
  const std::wstring wname = win::widen(name);
  HANDLE h = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, wname.c_str());
  if (h == nullptr) {
    if (error) *error = "OpenEvent " + name + " failed: " + win::last_error_message();
    return nullptr;
  }
  auto e = std::make_unique<NamedEvent>();
  e->handle_.reset(h);
  return e;
}

std::unique_ptr<CaptureRingMapping> CaptureRingMapping::create(ObjectNamespace ns, const std::string& desk,
                                                               const capture::RingFormat& format, std::string* error) {
  auto m = std::make_unique<CaptureRingMapping>();
  const auto security = default_security(ns);
  m->section = SharedSection::create(capture_ring_section_name(ns, desk), sizeof(SpCaptureRingHeader), security, error);
  if (!m->section) return nullptr;
  if (m->section->existed()) log_warn("capture ring {} already existed; reinitialising", m->section->name());
  m->frame_ready = NamedEvent::create(frame_ready_event_name(ns, desk), /*auto_reset=*/true, security, error);
  if (!m->frame_ready) return nullptr;
  capture::init_header(m->header(), format, GetCurrentProcessId());
  return m;
}

std::unique_ptr<CaptureRingMapping> CaptureRingMapping::open(ObjectNamespace ns, const std::string& desk,
                                                             std::string* error) {
  auto m = std::make_unique<CaptureRingMapping>();
  m->section = SharedSection::open(capture_ring_section_name(ns, desk), error);
  if (!m->section) return nullptr;
  if (!capture::validate_header(m->header(), m->section->size(), error)) return nullptr;
  m->frame_ready = NamedEvent::open(frame_ready_event_name(ns, desk), error);
  if (!m->frame_ready) return nullptr;
  return m;
}

std::unique_ptr<InputRingMapping> InputRingMapping::create(ObjectNamespace ns, const std::string& desk, uint32_t flags,
                                                           std::string* error) {
  auto m = std::make_unique<InputRingMapping>();
  const auto security = default_security(ns);
  m->section = SharedSection::create(input_ring_section_name(ns, desk), sizeof(SpInputRing), security, error);
  if (!m->section) return nullptr;
  if (m->section->existed()) log_warn("input ring {} already existed; reinitialising", m->section->name());
  m->input_ready = NamedEvent::create(input_ready_event_name(ns, desk), /*auto_reset=*/true, security, error);
  if (!m->input_ready) return nullptr;
  input::init_ring(m->ring(), flags);
  m->ring()->consumer_pid = GetCurrentProcessId();
  return m;
}

std::unique_ptr<InputRingMapping> InputRingMapping::open(ObjectNamespace ns, const std::string& desk,
                                                         std::string* error) {
  auto m = std::make_unique<InputRingMapping>();
  m->section = SharedSection::open(input_ring_section_name(ns, desk), error);
  if (!m->section) return nullptr;
  if (!input::validate_ring(m->ring(), m->section->size(), error)) return nullptr;
  m->input_ready = NamedEvent::open(input_ready_event_name(ns, desk), error);
  if (!m->input_ready) return nullptr;
  m->ring()->producer_pid = GetCurrentProcessId();
  return m;
}

}  // namespace splitplay::shm
