// SPDX-License-Identifier: MIT
// DLL entry points of splitplay-rdsprov.dll. Exported through rdsprov.def.
#include <new>

#include "rdsprov.hpp"

using namespace splitplay::rdsprov;

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
  (void)reserved;
  if (reason == DLL_PROCESS_ATTACH) {
    set_module_handle(instance);
    DisableThreadLibraryCalls(instance);
  }
  return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* object) {
  if (!object) return E_POINTER;
  *object = nullptr;
  if (!IsEqualCLSID(clsid, kClsidProtocolProvider)) return CLASS_E_CLASSNOTAVAILABLE;
  auto* factory = new (std::nothrow) ClassFactory();
  if (!factory) return E_OUTOFMEMORY;
  const HRESULT hr = factory->QueryInterface(riid, object);
  factory->Release();
  return hr;
}

STDAPI DllCanUnloadNow() { return module_object_count() == 0 && module_lock_count() == 0 ? S_OK : S_FALSE; }

// Stage 2: registration is deliberately a no-op. Stage 4 registers the CLSID and swaps it into TermService's
// protocol provider key (with a backup of the original rdpcorets registration) from the installer, not from here.
STDAPI DllRegisterServer() { return S_OK; }

STDAPI DllUnregisterServer() { return S_OK; }
