// SPDX-License-Identifier: MIT
// Host-side smoke tests for the driver logic that does not need a device: session id parsing, the mode list,
// the DS4 HID report descriptor, IOCTL layouts and the INF files. Runs on the CI runner (nothing is installed)
// and on any developer machine (portable C++17; also builds with g++/clang on Linux).
//
// Usage: SplitPlay.Drivers.Tests [<path to drivers/ directory>]
//   With the path, the INF files are checked for the settings the drivers depend on.

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <string>
#include <utility>

#include "../idd/include/splitplay/idd_ioctl.h"
#include "../idd/src/ModeList.h"
#include "../idd/src/SessionId.h"
#include "../pad/include/splitplay/pad_ioctl.h"
#include "../pad/src/Ds4.h"
#include "splitplay/capture_ring.h"
#include "splitplay/trace.h"

namespace {

int g_failures = 0;
int g_checks = 0;

void Check(bool condition, const char* what, int line) {
  ++g_checks;
  if (!condition) {
    ++g_failures;
    std::printf("FAIL line %d: %s\n", line, what);
  }
}
#define CHECK(cond) Check((cond), #cond, __LINE__)

// ---- IDD: session id ---------------------------------------------------------------------------------------

void TestSessionId() {
  using splitplay::idd::ParseSessionIdFromInstanceId;
  uint32_t id = 0;
  CHECK(ParseSessionIdFromInstanceId(L"SWD\\RemoteDisplayEnum\\SplitPlay_RemoteIdd&SessionId_0003", &id) && id == 3);
  CHECK(ParseSessionIdFromInstanceId(L"ROOT\\DISPLAY\\0000&sessionid_12", &id) && id == 12);
  CHECK(ParseSessionIdFromInstanceId(L"X&SESSIONID_0042&Other", &id) && id == 42);
  CHECK(ParseSessionIdFromInstanceId(L"A&SessionId_1&B&SessionId_0007", &id) && id == 7); // last marker wins
  CHECK(!ParseSessionIdFromInstanceId(L"SWD\\RemoteDisplayEnum\\NoSession", &id));
  CHECK(!ParseSessionIdFromInstanceId(L"X&SessionId_", &id));
  CHECK(!ParseSessionIdFromInstanceId(L"X&SessionId_abc", &id));
  CHECK(!ParseSessionIdFromInstanceId(L"X&SessionId_99999999999", &id));
  CHECK(!ParseSessionIdFromInstanceId(nullptr, &id));
}

void TestDeskId() {
  using splitplay::idd::IsValidDeskId;
  char buf[SP_IDD_DESK_ID_MAX + 1] = {};
  std::strcpy(buf, "desk-1");
  CHECK(IsValidDeskId(buf, sizeof(buf)));
  std::strcpy(buf, "Gaming");
  CHECK(!IsValidDeskId(buf, sizeof(buf)));
  std::strcpy(buf, "");
  CHECK(!IsValidDeskId(buf, sizeof(buf)));
  std::memset(buf, 'a', SP_IDD_DESK_ID_MAX);
  buf[SP_IDD_DESK_ID_MAX] = '\0';
  CHECK(IsValidDeskId(buf, sizeof(buf)));
  std::memset(buf, 'a', sizeof(buf)); // no terminator inside the buffer
  CHECK(!IsValidDeskId(buf, sizeof(buf)));
  CHECK(sp_desk_hash("desk-1") == sp_desk_hash("desk-1"));
  CHECK(sp_desk_hash("desk-1") != sp_desk_hash("desk-2"));
}

// ---- IDD: modes --------------------------------------------------------------------------------------------

void TestModes() {
  using namespace splitplay::idd;
  CHECK(kModeCount == kResolutionCount * kRefreshRateCount);
  CHECK(kModeCount > 0 && kModeCount < 1024);
  bool has500 = false;
  bool has1080p500 = false;
  uint32_t maxHz = 0;
  for (uint32_t i = 0; i < kModeCount; ++i) {
    const Mode m = ModeAt(i);
    CHECK(m.width >= 640 && m.height >= 480);
    has500 = has500 || m.refreshHz == 500;
    has1080p500 = has1080p500 || (m.width == 1920 && m.height == 1080 && m.refreshHz == 500);
    maxHz = m.refreshHz > maxHz ? m.refreshHz : maxHz;
    CHECK(IndexOf(m) == i); // no duplicates
  }
  CHECK(has500 && has1080p500);
  CHECK(maxHz == kMaxRefreshHz);
  const Mode preferred = ModeAt(kPreferredModeIndex);
  CHECK(preferred.width == 1920 && preferred.height == 1080 && preferred.refreshHz == 60);
  CHECK(IsSupported(1920, 1080, 500, 1));
  CHECK(IsSupported(2560, 1440, 240, 1));
  CHECK(IsSupported(1920, 1080, 120000, 1000));
  CHECK(!IsSupported(1920, 1080, 59940, 1000)); // fractional rates are not listed
  CHECK(!IsSupported(1921, 1080, 60, 1));
  CHECK(!IsSupported(1920, 1080, 60, 0));
}

// ---- Pad: DS4 descriptor -----------------------------------------------------------------------------------

struct ParsedDescriptor {
  bool ok = true;
  int maxDepth = 0;
  std::map<std::pair<int, int>, uint32_t> bits; // (main tag, report id) -> bits
};

// Minimal HID short-item parser: tracks Report Size/Count/ID and sums bits per main item and report id.
ParsedDescriptor ParseHid(const uint8_t* d, uint32_t n) {
  ParsedDescriptor out;
  uint32_t reportSize = 0, reportCount = 0, reportId = 0;
  int depth = 0;
  for (uint32_t i = 0; i < n;) {
    const uint8_t prefix = d[i];
    if (prefix == 0xFE) { // long item: not used by our descriptor
      out.ok = false;
      return out;
    }
    const uint32_t size = (prefix & 3u) == 3u ? 4u : (prefix & 3u);
    const uint32_t type = (prefix >> 2) & 3u;
    const uint32_t tag = prefix >> 4;
    if (i + 1 + size > n) {
      out.ok = false;
      return out;
    }
    uint32_t value = 0;
    for (uint32_t b = 0; b < size; ++b) {
      value |= static_cast<uint32_t>(d[i + 1 + b]) << (8 * b);
    }
    if (type == 0) { // main
      if (tag == 0x8 || tag == 0x9 || tag == 0xB) {
        out.bits[{static_cast<int>(tag), static_cast<int>(reportId)}] += reportSize * reportCount;
      } else if (tag == 0xA) {
        ++depth;
        out.maxDepth = depth > out.maxDepth ? depth : out.maxDepth;
      } else if (tag == 0xC) {
        --depth;
        if (depth < 0) out.ok = false;
      }
    } else if (type == 1) { // global
      if (tag == 0x7) reportSize = value;
      if (tag == 0x8) reportId = value;
      if (tag == 0x9) reportCount = value;
    }
    i += 1 + size;
  }
  if (depth != 0) out.ok = false;
  return out;
}

void TestDs4Descriptor() {
  using namespace splitplay::pad;
  const ParsedDescriptor p = ParseHid(kDs4ReportDescriptor, kDs4ReportDescriptorSize);
  CHECK(p.ok);
  CHECK(p.maxDepth == 1);
  constexpr int kInput = 0x8, kOutput = 0x9, kFeature = 0xB;
  auto bits = [&p](int tag, int id) {
    auto it = p.bits.find({tag, id});
    return it == p.bits.end() ? 0u : it->second;
  };
  CHECK(bits(kInput, kDs4InputReportId) == (kDs4InputReportSize - 1) * 8);
  CHECK(bits(kOutput, kDs4OutputReportId) == (kDs4OutputReportSize - 1) * 8);
  CHECK(bits(kFeature, kDs4FeatureCalibrationId) == (kDs4CalibrationSize - 1) * 8);
  CHECK(bits(kFeature, kDs4FeaturePairingId) == (kDs4PairingSize - 1) * 8);
  CHECK(bits(kFeature, kDs4FeatureFirmwareId) == (kDs4FirmwareSize - 1) * 8);
  CHECK(p.bits.size() == 5); // nothing outside the five reports, no data without a report id
  CHECK(kDs4ReportDescriptorSize < 0xFFFF); // VHF_CONFIG.ReportDescriptorLength is a USHORT

  uint8_t report[kDs4InputReportSize];
  InitNeutralInputReport(report);
  CHECK(report[0] == kDs4InputReportId);
  CHECK(report[kDs4OffLeftX] == 0x80 && report[kDs4OffRightY] == 0x80);
  CHECK((report[kDs4OffHatButtons] & 0x0F) == kDs4HatReleased);
  CHECK(report[kDs4OffButtons1] == 0 && report[kDs4OffL2] == 0);

  uint8_t calibration[kDs4CalibrationSize];
  FillCalibrationReport(calibration);
  CHECK(calibration[0] == kDs4FeatureCalibrationId);
  const int16_t gyroPlus = static_cast<int16_t>(calibration[7] | (calibration[8] << 8));
  const int16_t accelMinusZ = static_cast<int16_t>(calibration[33] | (calibration[34] << 8));
  CHECK(gyroPlus == 8192 && accelMinusZ == -8192);

  const uint8_t mac[6] = {1, 2, 3, 4, 5, 6};
  uint8_t pairing[kDs4PairingSize];
  FillPairingReport(pairing, mac);
  CHECK(pairing[0] == kDs4FeaturePairingId && pairing[1] == 1 && pairing[6] == 6);
}

// ---- IOCTL contracts ---------------------------------------------------------------------------------------

void TestIoctls() {
  const uint32_t idd[] = {SP_IOCTL_IDD_GET_INFO, SP_IOCTL_IDD_ATTACH_DESK, SP_IOCTL_IDD_SET_MODE,
                          SP_IOCTL_IDD_SET_RENDER_ADAPTER};
  const uint32_t pad[] = {SP_IOCTL_PAD_CREATE, SP_IOCTL_PAD_SUBMIT, SP_IOCTL_PAD_DESTROY, SP_IOCTL_PAD_GET_FEEDBACK};
  for (uint32_t a : idd) {
    CHECK((a >> 16) == SP_IDD_DEVICE_TYPE);
    CHECK((a & 3u) == 0u); // METHOD_BUFFERED
    for (uint32_t b : pad) CHECK(a != b);
  }
  for (uint32_t a : pad) {
    CHECK((a >> 16) == SP_PAD_DEVICE_TYPE);
    CHECK((a & 3u) == 0u);
  }
  // CTL_CODE(0x9A10, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS) computed by hand.
  CHECK(SP_IOCTL_IDD_GET_INFO == 0x9A102000u);
  CHECK(SP_IOCTL_PAD_SUBMIT == 0x9A11A004u);
  CHECK(SP_PAD_MAX_REPORT >= splitplay::pad::kDs4InputReportSize);
  CHECK(sizeof(SpCaptureSlot) == 128 && SP_CAPTURE_SLOTS >= 2);
}

// ---- INF files ---------------------------------------------------------------------------------------------

std::string ReadFile(const std::string& path) {
  std::ifstream f(path, std::ios::binary);
  std::ostringstream s;
  s << f.rdbuf();
  return s.str();
}

void TestInfs(const std::string& driversDir) {
  const std::string idd = ReadFile(driversDir + "/idd/SplitPlayIdd.inf");
  const std::string pad = ReadFile(driversDir + "/pad/SplitPlayPad.inf");
  CHECK(!idd.empty() && !pad.empty());
  auto has = [](const std::string& text, const char* needle) { return text.find(needle) != std::string::npos; };
  CHECK(has(idd, SP_IDD_HARDWARE_ID));
  CHECK(has(idd, "UmdfHostProcessSharing     = ProcessSharingDisabled"));
  CHECK(has(idd, "UmdfExtensions     = IddCx0102"));
  CHECK(has(idd, "\"IndirectKmd\""));
  CHECK(has(idd, "Class       = Display"));
  CHECK(has(pad, SP_PAD_HARDWARE_ID));
  CHECK(has(pad, "vhfservice.NT"));
  CHECK(has(pad, "WUDFRD_UpperFilter.NT"));
}

} // namespace

int main(int argc, char** argv) {
  TestSessionId();
  TestDeskId();
  TestModes();
  TestDs4Descriptor();
  TestIoctls();
  if (argc > 1) {
    TestInfs(argv[1]);
  }
  std::printf("%d checks, %d failures (modes: %u, DS4 descriptor: %u bytes)\n", g_checks, g_failures,
              splitplay::idd::kModeCount, splitplay::pad::kDs4ReportDescriptorSize);
  return g_failures == 0 ? 0 : 1;
}
