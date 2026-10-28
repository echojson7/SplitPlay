// SPDX-License-Identifier: MIT
// ETW tracing for SplitPlayIdd.
//
// Uses the shared TraceLogging provider "SplitPlay" from native/include/splitplay/trace.h, so that one ETW
// session sees the IDD, worker and seat stages of the same frame_id. Diagnostic (non-frame) events use the
// same provider with event names prefixed "Idd".
//   Capture:  tracelog -start sp -guid #6B2E3F5A-9C41-4D7B-A1E2-53504C415931 -f sp.etl
#pragma once

#include <windows.h>
#include <TraceLoggingProvider.h>
#include <winmeta.h> // WINEVENT_LEVEL_*

#include "splitplay/trace.h"

TRACELOGGING_DECLARE_PROVIDER(g_SpTraceProvider);

// Diagnostic event with an NTSTATUS/HRESULT, e.g. SP_IDD_LOG("IddAdapterInit", status).
#define SP_IDD_LOG(eventName, code)                                           \
  TraceLoggingWrite(g_SpTraceProvider, eventName,                             \
                    TraceLoggingLevel(WINEVENT_LEVEL_INFO),                   \
                    TraceLoggingHexInt32(static_cast<INT32>(code), "status"))

// Per-frame stage record (SpTraceRecord in trace.h), event name "Frame".
#define SP_IDD_TRACE_FRAME(frameId, qpc, stage, deskHash)                     \
  TraceLoggingWrite(g_SpTraceProvider, "Frame",                               \
                    TraceLoggingLevel(WINEVENT_LEVEL_VERBOSE),                \
                    TraceLoggingUInt64((frameId), "frame_id"),                \
                    TraceLoggingUInt64((qpc), "qpc"),                         \
                    TraceLoggingUInt32((stage), "stage"),                     \
                    TraceLoggingUInt32((deskHash), "desk_hash"))
