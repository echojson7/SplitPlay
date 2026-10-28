// SPDX-License-Identifier: MIT
// D3D11 device helpers for the capture ring: adapter selection (hardware, WARP), LUID lookup, shared fences by name.
#pragma once

#ifdef _WIN32

#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace splitplay::pipeline {

using Microsoft::WRL::ComPtr;

enum class AdapterChoice {
  automatic,  // the default (first) adapter, falling back to WARP
  hardware,   // the default (first) adapter only
  warp,       // the WARP software rasteriser (works on GPU-less CI runners)
};

bool parse_adapter_choice(std::string_view text, AdapterChoice& out) noexcept;
std::string_view to_string(AdapterChoice choice) noexcept;

struct D3DDevice {
  ComPtr<ID3D11Device5> device;           // D3D11.4 for shared fences
  ComPtr<ID3D11DeviceContext4> context;
  ComPtr<IDXGIAdapter1> adapter;
  uint64_t luid = 0;
  std::string description;
  bool software = false;
};

uint64_t luid_to_u64(const LUID& luid) noexcept;
LUID u64_to_luid(uint64_t value) noexcept;

std::optional<D3DDevice> create_device(AdapterChoice choice, std::string* error);
// Creates a device on the adapter with the given LUID (the adapter the producer's textures live on).
std::optional<D3DDevice> create_device_for_luid(uint64_t luid, std::string* error);

// Opens a fence that the producer shared by name. D3D11 has no open-by-name for fences, so the name is resolved to an
// NT handle with ID3D12Device::OpenSharedHandleByName (d3d12.dll loaded on demand). Returns nullptr with `error` set
// if D3D12 is unavailable on this adapter.
ComPtr<ID3D11Fence> open_shared_fence_by_name(const D3DDevice& device, const std::wstring& name, std::string* error);

}  // namespace splitplay::pipeline

#endif  // _WIN32
