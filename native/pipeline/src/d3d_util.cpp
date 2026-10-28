// SPDX-License-Identifier: MIT
#include "splitplay/pipeline/d3d_util.hpp"

#include <d3d12.h>

#include <format>

#include "splitplay/common/win_util.hpp"

namespace splitplay::pipeline {

bool parse_adapter_choice(std::string_view text, AdapterChoice& out) noexcept {
  if (text == "auto") {
    out = AdapterChoice::automatic;
  } else if (text == "hardware" || text == "default") {
    out = AdapterChoice::hardware;
  } else if (text == "warp") {
    out = AdapterChoice::warp;
  } else {
    return false;
  }
  return true;
}

std::string_view to_string(AdapterChoice choice) noexcept {
  switch (choice) {
    case AdapterChoice::automatic: return "auto";
    case AdapterChoice::hardware: return "default";
    case AdapterChoice::warp: return "warp";
  }
  return "auto";
}

uint64_t luid_to_u64(const LUID& luid) noexcept {
  return (static_cast<uint64_t>(static_cast<uint32_t>(luid.HighPart)) << 32) | luid.LowPart;
}

LUID u64_to_luid(uint64_t value) noexcept {
  LUID luid;
  luid.LowPart = static_cast<DWORD>(value & 0xffffffffu);
  luid.HighPart = static_cast<LONG>(static_cast<uint32_t>(value >> 32));
  return luid;
}

namespace {

std::optional<D3DDevice> create_on_adapter(ComPtr<IDXGIAdapter1> adapter, std::string* error) {
  DXGI_ADAPTER_DESC1 desc{};
  adapter->GetDesc1(&desc);
  static const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
  ComPtr<ID3D11Device> device;
  ComPtr<ID3D11DeviceContext> context;
  const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
  HRESULT hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags, levels,
                                 static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION, &device, nullptr, &context);
  if (FAILED(hr)) {
    if (error) *error = "D3D11CreateDevice failed: " + win::hresult_message(hr);
    return std::nullopt;
  }
  D3DDevice out;
  if (FAILED(hr = device.As(&out.device)) || FAILED(hr = context.As(&out.context))) {
    if (error) *error = "D3D11.4 (ID3D11Device5) is not available: " + win::hresult_message(hr);
    return std::nullopt;
  }
  ComPtr<ID3D11Multithread> mt;
  if (SUCCEEDED(context.As(&mt))) mt->SetMultithreadProtected(TRUE);
  out.adapter = adapter;
  out.luid = luid_to_u64(desc.AdapterLuid);
  out.description = win::narrow(desc.Description);
  out.software = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
  return out;
}

ComPtr<IDXGIFactory4> make_factory(std::string* error) {
  ComPtr<IDXGIFactory4> factory;
  const HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
  if (FAILED(hr) && error) *error = "CreateDXGIFactory1 failed: " + win::hresult_message(hr);
  return factory;
}

}  // namespace

std::optional<D3DDevice> create_device(AdapterChoice choice, std::string* error) {
  auto factory = make_factory(error);
  if (!factory) return std::nullopt;
  std::string hw_error;
  if (choice != AdapterChoice::warp) {
    ComPtr<IDXGIAdapter1> adapter;
    HRESULT hr = factory->EnumAdapters1(0, &adapter);
    if (SUCCEEDED(hr)) {
      if (auto dev = create_on_adapter(adapter, &hw_error)) return dev;
    } else {
      hw_error = "no DXGI adapter: " + win::hresult_message(hr);
    }
    if (choice == AdapterChoice::hardware) {
      if (error) *error = hw_error;
      return std::nullopt;
    }
  }
  ComPtr<IDXGIAdapter1> warp;
  HRESULT hr = factory->EnumWarpAdapter(IID_PPV_ARGS(&warp));
  if (FAILED(hr)) {
    if (error) *error = std::format("EnumWarpAdapter failed: {}{}", win::hresult_message(hr),
                                    hw_error.empty() ? "" : "; default adapter: " + hw_error);
    return std::nullopt;
  }
  return create_on_adapter(warp, error);
}

std::optional<D3DDevice> create_device_for_luid(uint64_t luid, std::string* error) {
  auto factory = make_factory(error);
  if (!factory) return std::nullopt;
  ComPtr<IDXGIAdapter1> adapter;
  HRESULT hr = factory->EnumAdapterByLuid(u64_to_luid(luid), IID_PPV_ARGS(&adapter));
  if (FAILED(hr)) {
    // WARP is not always enumerated by LUID; compare explicitly.
    ComPtr<IDXGIAdapter1> warp;
    DXGI_ADAPTER_DESC1 desc{};
    if (SUCCEEDED(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp))) && SUCCEEDED(warp->GetDesc1(&desc)) &&
        luid_to_u64(desc.AdapterLuid) == luid) {
      adapter = warp;
    } else {
      if (error) *error = std::format("no adapter with LUID 0x{:016x}: {}", luid, win::hresult_message(hr));
      return std::nullopt;
    }
  }
  return create_on_adapter(adapter, error);
}

ComPtr<ID3D11Fence> open_shared_fence_by_name(const D3DDevice& device, const std::wstring& name, std::string* error) {
  static HMODULE d3d12 = LoadLibraryExW(L"d3d12.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
  if (!d3d12) {
    if (error) *error = "d3d12.dll is not available";
    return nullptr;
  }
  auto create = reinterpret_cast<PFN_D3D12_CREATE_DEVICE>(
      reinterpret_cast<void*>(GetProcAddress(d3d12, "D3D12CreateDevice")));
  if (!create) {
    if (error) *error = "D3D12CreateDevice not found";
    return nullptr;
  }
  ComPtr<ID3D12Device> d12;
  HRESULT hr = create(device.adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&d12));
  if (FAILED(hr)) {
    if (error) *error = "D3D12CreateDevice failed: " + win::hresult_message(hr);
    return nullptr;
  }
  HANDLE handle = nullptr;
  hr = d12->OpenSharedHandleByName(name.c_str(), GENERIC_ALL, &handle);
  if (FAILED(hr)) {
    if (error) *error = "OpenSharedHandleByName(" + win::narrow(name) + ") failed: " + win::hresult_message(hr);
    return nullptr;
  }
  win::UniqueHandle owned(handle);
  ComPtr<ID3D11Fence> fence;
  hr = device.device->OpenSharedFence(handle, IID_PPV_ARGS(&fence));
  if (FAILED(hr)) {
    if (error) *error = "OpenSharedFence failed: " + win::hresult_message(hr);
    return nullptr;
  }
  return fence;
}

}  // namespace splitplay::pipeline
