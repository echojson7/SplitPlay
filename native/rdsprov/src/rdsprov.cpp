// SPDX-License-Identifier: MIT
#include "rdsprov.hpp"

#include <new>

namespace splitplay::rdsprov {

namespace {
std::atomic<long> g_objects{0};
std::atomic<long> g_locks{0};
HMODULE g_module = nullptr;
}  // namespace

void module_add_ref() noexcept { ++g_objects; }
void module_release() noexcept { --g_objects; }
long module_object_count() noexcept { return g_objects.load(); }
void module_lock(bool lock) noexcept {
  if (lock) {
    ++g_locks;
  } else {
    --g_locks;
  }
}
long module_lock_count() noexcept { return g_locks.load(); }
HMODULE module_handle() noexcept { return g_module; }
void set_module_handle(HMODULE module) noexcept { g_module = module; }

// ---- ProtocolManagerWrapper ------------------------------------------------------------------------

ProtocolManagerWrapper::ProtocolManagerWrapper() noexcept { module_add_ref(); }

ProtocolManagerWrapper::~ProtocolManagerWrapper() {
  if (inner_) inner_->Release();
  module_release();
}

HRESULT STDMETHODCALLTYPE ProtocolManagerWrapper::QueryInterface(REFIID riid, void** object) {
  if (!object) return E_POINTER;
  *object = nullptr;
  if (IsEqualIID(riid, IID_IUnknown)) {
    *object = static_cast<IUnknown*>(this);
    AddRef();
    return S_OK;
  }
  // Stage 4: IWRdsProtocolManager (delegated to inner_) and IWRdsWddmIddProps.
  return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE ProtocolManagerWrapper::AddRef() { return ++refs_; }

ULONG STDMETHODCALLTYPE ProtocolManagerWrapper::Release() {
  const ULONG n = --refs_;
  if (n == 0) delete this;
  return n;
}

HRESULT ProtocolManagerWrapper::load_inner() noexcept { return E_NOTIMPL; }

// ---- ClassFactory ----------------------------------------------------------------------------------

ClassFactory::ClassFactory() noexcept { module_add_ref(); }
ClassFactory::~ClassFactory() { module_release(); }

HRESULT STDMETHODCALLTYPE ClassFactory::QueryInterface(REFIID riid, void** object) {
  if (!object) return E_POINTER;
  *object = nullptr;
  if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory)) {
    *object = static_cast<IClassFactory*>(this);
    AddRef();
    return S_OK;
  }
  return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE ClassFactory::AddRef() { return ++refs_; }

ULONG STDMETHODCALLTYPE ClassFactory::Release() {
  const ULONG n = --refs_;
  if (n == 0) delete this;
  return n;
}

HRESULT STDMETHODCALLTYPE ClassFactory::CreateInstance(IUnknown* outer, REFIID riid, void** object) {
  if (!object) return E_POINTER;
  *object = nullptr;
  if (outer) return CLASS_E_NOAGGREGATION;
  auto* wrapper = new (std::nothrow) ProtocolManagerWrapper();
  if (!wrapper) return E_OUTOFMEMORY;
  const HRESULT hr = wrapper->QueryInterface(riid, object);
  wrapper->Release();
  return hr;
}

HRESULT STDMETHODCALLTYPE ClassFactory::LockServer(BOOL lock) {
  module_lock(lock != FALSE);
  return S_OK;
}

}  // namespace splitplay::rdsprov
