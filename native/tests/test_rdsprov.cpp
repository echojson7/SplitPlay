// SPDX-License-Identifier: MIT
// Loads splitplay-rdsprov.dll like COM would and exercises its exports. Registration must be a no-op.
#include <windows.h>
#include <unknwn.h>

#include <string>

#include "rdsprov.hpp"
#include "sp_test.hpp"

namespace {

using GetClassObjectFn = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
using SimpleFn = HRESULT(STDAPICALLTYPE*)();

struct Dll {
  HMODULE module = nullptr;
  GetClassObjectFn get_class_object = nullptr;
  SimpleFn can_unload = nullptr;
  SimpleFn register_server = nullptr;
  SimpleFn unregister_server = nullptr;

  Dll() {
    // The DLL is built next to the test executable.
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring dll(path, n);
    dll = dll.substr(0, dll.find_last_of(L"\\/") + 1) + L"splitplay-rdsprov.dll";
    module = LoadLibraryW(dll.c_str());
    if (!module) return;
    get_class_object = reinterpret_cast<GetClassObjectFn>(reinterpret_cast<void*>(GetProcAddress(module, "DllGetClassObject")));
    can_unload = reinterpret_cast<SimpleFn>(reinterpret_cast<void*>(GetProcAddress(module, "DllCanUnloadNow")));
    register_server = reinterpret_cast<SimpleFn>(reinterpret_cast<void*>(GetProcAddress(module, "DllRegisterServer")));
    unregister_server = reinterpret_cast<SimpleFn>(reinterpret_cast<void*>(GetProcAddress(module, "DllUnregisterServer")));
  }
  ~Dll() {
    if (module) FreeLibrary(module);
  }
};

}  // namespace

TEST_CASE("exports exist") {
  Dll dll;
  REQUIRE(dll.module != nullptr);
  CHECK(dll.get_class_object != nullptr);
  CHECK(dll.can_unload != nullptr);
  CHECK(dll.register_server != nullptr);
  CHECK(dll.unregister_server != nullptr);
}

TEST_CASE("class object creates the wrapper and the module tracks lifetimes") {
  Dll dll;
  REQUIRE(dll.get_class_object && dll.can_unload);
  CHECK(dll.can_unload() == S_OK);

  IClassFactory* factory = nullptr;
  CHECK(dll.get_class_object(CLSID_NULL, IID_IClassFactory, reinterpret_cast<void**>(&factory)) ==
        CLASS_E_CLASSNOTAVAILABLE);
  REQUIRE(dll.get_class_object(splitplay::rdsprov::kClsidProtocolProvider, IID_IClassFactory,
                               reinterpret_cast<void**>(&factory)) == S_OK);
  REQUIRE(factory != nullptr);
  CHECK(dll.can_unload() == S_FALSE);

  IUnknown* obj = nullptr;
  CHECK(factory->CreateInstance(reinterpret_cast<IUnknown*>(factory), IID_IUnknown, reinterpret_cast<void**>(&obj)) ==
        CLASS_E_NOAGGREGATION);
  REQUIRE(factory->CreateInstance(nullptr, IID_IUnknown, reinterpret_cast<void**>(&obj)) == S_OK);
  REQUIRE(obj != nullptr);
  IClassFactory* not_supported = nullptr;
  CHECK(obj->QueryInterface(IID_IClassFactory, reinterpret_cast<void**>(&not_supported)) == E_NOINTERFACE);
  CHECK(not_supported == nullptr);

  CHECK(factory->LockServer(TRUE) == S_OK);
  factory->Release();
  obj->Release();
  CHECK(dll.can_unload() == S_FALSE);  // still locked
  // LockServer(FALSE) needs a factory again.
  REQUIRE(dll.get_class_object(splitplay::rdsprov::kClsidProtocolProvider, IID_IClassFactory,
                               reinterpret_cast<void**>(&factory)) == S_OK);
  CHECK(factory->LockServer(FALSE) == S_OK);
  factory->Release();
  CHECK(dll.can_unload() == S_OK);
}

TEST_CASE("registration is a no-op in Stage 2") {
  Dll dll;
  REQUIRE(dll.register_server && dll.unregister_server);
  CHECK(dll.register_server() == S_OK);
  CHECK(dll.unregister_server() == S_OK);
  // Nothing may have been written for our CLSID.
  HKEY key = nullptr;
  const LONG rc = RegOpenKeyExW(HKEY_CLASSES_ROOT, L"CLSID\\{0727C011-24CC-4B88-BD7E-322F2AB6FAA8}", 0, KEY_READ, &key);
  if (key) RegCloseKey(key);
  CHECK(rc == ERROR_FILE_NOT_FOUND);
}

SP_TEST_MAIN()
