// SPDX-License-Identifier: MIT
// splitplay-rdsprov.dll: in-proc COM server loaded by TermService (Stage 4). It will wrap Microsoft's rdpcorets
// protocol provider, delegate everything to it, and answer IWRdsWddmIddProps with the SplitPlay IDD hardware id for
// SplitPlay loopback connections (ARCHITECTURE.md §2).
//
// Stage 2 is only the COM skeleton: the class object and an IUnknown-only wrapper. It does not load rdpcorets,
// does not register itself and does not touch TermService or its registry keys.
#pragma once

#include <windows.h>
#include <unknwn.h>

#include <atomic>

namespace splitplay::rdsprov {

// {0727C011-24CC-4B88-BD7E-322F2AB6FAA8}
inline constexpr CLSID kClsidProtocolProvider = {
    0x0727c011, 0x24cc, 0x4b88, {0xbd, 0x7e, 0x32, 0x2f, 0x2a, 0xb6, 0xfa, 0xa8}};

// Live COM objects and server locks; DllCanUnloadNow returns S_OK when both are zero.
void module_add_ref() noexcept;
void module_release() noexcept;
long module_object_count() noexcept;
void module_lock(bool lock) noexcept;
long module_lock_count() noexcept;
HMODULE module_handle() noexcept;
void set_module_handle(HMODULE module) noexcept;

// The future rdpcorets wrapper. Stage 4 adds IWRdsProtocolManager (all calls delegated to the inner provider) and
// IWRdsWddmIddProps; Stage 2 implements IUnknown only, so QueryInterface for anything else fails with E_NOINTERFACE.
class ProtocolManagerWrapper final : public IUnknown {
 public:
  ProtocolManagerWrapper() noexcept;

  // IUnknown
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override;
  ULONG STDMETHODCALLTYPE AddRef() override;
  ULONG STDMETHODCALLTYPE Release() override;

  // Placeholder for Stage 4: CoCreateInstance of the original rdpcorets provider (CLSID read from the backed-up
  // TermService registration). Not called in Stage 2; returns E_NOTIMPL.
  HRESULT load_inner() noexcept;

 private:
  ~ProtocolManagerWrapper();
  std::atomic<ULONG> refs_{1};
  IUnknown* inner_ = nullptr;  // rdpcorets provider, owned (Stage 4)
};

class ClassFactory final : public IClassFactory {
 public:
  ClassFactory() noexcept;

  // IUnknown
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override;
  ULONG STDMETHODCALLTYPE AddRef() override;
  ULONG STDMETHODCALLTYPE Release() override;

  // IClassFactory
  HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** object) override;
  HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override;

 private:
  ~ClassFactory();
  std::atomic<ULONG> refs_{1};
};

}  // namespace splitplay::rdsprov
