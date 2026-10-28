// SPDX-License-Identifier: MIT
// Atomic access to the `volatile` fields of the shared-memory contracts (capture_ring.h, input_ring.h).
//
// The C headers mark cross-process fields `volatile` so that C code (and the IDD) sees them as shared. `volatile`
// alone gives no ordering guarantees on ARM64 (MSVC /volatile:iso), so C++ code accesses them through
// std::atomic_ref with explicit memory orders. Every field used here is naturally aligned in the fixed layouts.
#pragma once

#include <atomic>
#include <cstdint>
#include <type_traits>

namespace splitplay::shm {

template <class T>
inline std::atomic_ref<T> ref(volatile T& field) noexcept {
  static_assert(std::is_integral_v<T>, "shared fields are integers");
  static_assert(std::atomic_ref<T>::is_always_lock_free, "shared fields must be lock-free");
  return std::atomic_ref<T>(const_cast<T&>(field));
}

template <class T>
inline std::atomic_ref<T> ref(T& field) noexcept {
  static_assert(std::is_integral_v<T>, "shared fields are integers");
  static_assert(std::atomic_ref<T>::is_always_lock_free, "shared fields must be lock-free");
  return std::atomic_ref<T>(field);
}

template <class T>
inline T load_acquire(volatile T& field) noexcept {
  return ref(field).load(std::memory_order_acquire);
}
template <class T>
inline T load_relaxed(volatile T& field) noexcept {
  return ref(field).load(std::memory_order_relaxed);
}
template <class T>
inline T load_seq_cst(volatile T& field) noexcept {
  return ref(field).load(std::memory_order_seq_cst);
}
template <class T, class V>
inline void store_release(volatile T& field, V value) noexcept {
  ref(field).store(static_cast<T>(value), std::memory_order_release);
}
template <class T, class V>
inline void store_relaxed(volatile T& field, V value) noexcept {
  ref(field).store(static_cast<T>(value), std::memory_order_relaxed);
}
template <class T, class V>
inline void store_seq_cst(volatile T& field, V value) noexcept {
  ref(field).store(static_cast<T>(value), std::memory_order_seq_cst);
}

// Non-volatile fields that are nevertheless read concurrently (e.g. SpCaptureSlot::frame_id).
template <class T>
inline T load_relaxed_nv(T& field) noexcept {
  return ref(field).load(std::memory_order_relaxed);
}
template <class T, class V>
inline void store_relaxed_nv(T& field, V value) noexcept {
  ref(field).store(static_cast<T>(value), std::memory_order_relaxed);
}

}  // namespace splitplay::shm
