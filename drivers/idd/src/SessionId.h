// SPDX-License-Identifier: MIT
// Session and desk identity helpers for SplitPlayIdd.
//
// Kept free of Windows headers so that drivers/tests can exercise them on any compiler.
#pragma once

#include <stdint.h>
#include <stddef.h>

namespace splitplay::idd {

constexpr uint32_t kSessionUnknown = 0xFFFFFFFFu;

// The OS remote-desktop stack creates one remote display adapter devnode per session. The protocol
// provider's OnDriverLoad callback is not always invoked (docs/research/06-review.md, finding iv), so the
// driver derives its session from the device instance id, which ends in "...&SessionId_000N".
//
// Parses the decimal digits that follow the last "SessionId_" (ASCII case-insensitive) in `instanceId`.
// Returns false if the marker is missing, has no digits, or the value does not fit in 32 bits.
// NOTE: the digits are observed to be zero-padded decimal; confirm against a real devnode in the Stage 3
// lab test (if they turn out to be hex, only this function changes).
inline bool ParseSessionIdFromInstanceId(const wchar_t* instanceId, uint32_t* sessionId) {
  if (instanceId == nullptr || sessionId == nullptr) {
    return false;
  }
  static const char kMarker[] = "sessionid_";
  constexpr size_t kMarkerLen = sizeof(kMarker) - 1;

  const wchar_t* digits = nullptr;
  for (const wchar_t* p = instanceId; *p != L'\0'; ++p) {
    size_t i = 0;
    for (; i < kMarkerLen; ++i) {
      wchar_t c = p[i];
      if (c == L'\0') {
        break;
      }
      if (c >= L'A' && c <= L'Z') {
        c = static_cast<wchar_t>(c - L'A' + L'a');
      }
      if (c != static_cast<wchar_t>(kMarker[i])) {
        break;
      }
    }
    if (i == kMarkerLen) {
      digits = p + kMarkerLen; // keep scanning: the last marker wins
    }
  }
  if (digits == nullptr || *digits < L'0' || *digits > L'9') {
    return false;
  }

  uint64_t value = 0;
  for (; *digits >= L'0' && *digits <= L'9'; ++digits) {
    value = value * 10u + static_cast<uint64_t>(*digits - L'0');
    if (value >= kSessionUnknown) {
      return false;
    }
  }
  *sessionId = static_cast<uint32_t>(value);
  return true;
}

// Desk ids follow docs/contracts/README.md: lowercase [a-z0-9-]{1,32}. `length` excludes the terminator.
inline bool IsValidDeskId(const char* deskId, size_t capacity) {
  if (deskId == nullptr) {
    return false;
  }
  size_t length = 0;
  for (; length < capacity && deskId[length] != '\0'; ++length) {
    const char c = deskId[length];
    const bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
    if (!ok) {
      return false;
    }
  }
  return length >= 1 && length <= 32 && length < capacity;
}

} // namespace splitplay::idd
