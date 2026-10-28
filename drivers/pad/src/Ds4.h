// SPDX-License-Identifier: MIT
// DualShock 4 compatible HID profile for SplitPlayPad.
//
// The report descriptor below is written for SplitPlay and is layout-compatible with the DS4 USB reports that
// games, SDL (hidapi_ps4), Steam Input and DS4-aware titles parse by byte offset:
//   input   0x01: 63 data bytes (sticks, hat+buttons, counter, triggers, IMU, touchpad, battery)
//   output  0x05: 31 data bytes (rumble, lightbar)
//   feature 0x02: 36 data bytes (IMU calibration)
//   feature 0x12: 15 data bytes (pairing info / MAC address)
//   feature 0xA3: 48 data bytes (firmware info)
// Hosts identify a DS4 by VID/PID first; they read the calibration/pairing/firmware feature reports to accept it.
//
// Kept free of Windows headers so that drivers/tests can parse and check the descriptor on any compiler.
#pragma once

#include <stdint.h>

namespace splitplay::pad {

constexpr uint16_t kDs4VendorId = 0x054C;   // Sony Interactive Entertainment
constexpr uint16_t kDs4ProductId = 0x09CC;  // DualShock 4 (2nd generation, CUH-ZCT2)
constexpr uint16_t kDs4VersionNumber = 0x0100;

constexpr uint8_t kDs4InputReportId = 0x01;
constexpr uint8_t kDs4OutputReportId = 0x05;
constexpr uint8_t kDs4FeatureCalibrationId = 0x02;
constexpr uint8_t kDs4FeaturePairingId = 0x12;
constexpr uint8_t kDs4FeatureFirmwareId = 0xA3;

constexpr uint32_t kDs4InputReportSize = 64;   // including the report id
constexpr uint32_t kDs4OutputReportSize = 32;  // including the report id
constexpr uint32_t kDs4CalibrationSize = 37;
constexpr uint32_t kDs4PairingSize = 16;
constexpr uint32_t kDs4FirmwareSize = 49;

// clang-format off
constexpr uint8_t kDs4ReportDescriptor[] = {
  0x05, 0x01,             // Usage Page (Generic Desktop)
  0x09, 0x05,             // Usage (Game Pad)
  0xA1, 0x01,             // Collection (Application)

  // ---- Input report 0x01 --------------------------------------------------------------------------
  0x85, 0x01,             //   Report ID (1)
  0x09, 0x30,             //   Usage (X)           left stick X   byte 1
  0x09, 0x31,             //   Usage (Y)           left stick Y   byte 2
  0x09, 0x32,             //   Usage (Z)           right stick X  byte 3
  0x09, 0x35,             //   Usage (Rz)          right stick Y  byte 4
  0x15, 0x00,             //   Logical Minimum (0)
  0x26, 0xFF, 0x00,       //   Logical Maximum (255)
  0x75, 0x08,             //   Report Size (8)
  0x95, 0x04,             //   Report Count (4)
  0x81, 0x02,             //   Input (Data, Var, Abs)
  0x09, 0x39,             //   Usage (Hat Switch)  byte 5 bits 0-3, 8 = released
  0x15, 0x00,             //   Logical Minimum (0)
  0x25, 0x07,             //   Logical Maximum (7)
  0x35, 0x00,             //   Physical Minimum (0)
  0x46, 0x3B, 0x01,       //   Physical Maximum (315)
  0x65, 0x14,             //   Unit (Degrees)
  0x75, 0x04,             //   Report Size (4)
  0x95, 0x01,             //   Report Count (1)
  0x81, 0x42,             //   Input (Data, Var, Abs, Null State)
  0x65, 0x00,             //   Unit (None)
  0x05, 0x09,             //   Usage Page (Button)  byte 5 bits 4-7, byte 6, byte 7 bits 0-1
  0x19, 0x01,             //   Usage Minimum (1)    square, cross, circle, triangle, L1, R1, L2, R2,
  0x29, 0x0E,             //   Usage Maximum (14)   share, options, L3, R3, PS, touchpad click
  0x15, 0x00,             //   Logical Minimum (0)
  0x25, 0x01,             //   Logical Maximum (1)
  0x75, 0x01,             //   Report Size (1)
  0x95, 0x0E,             //   Report Count (14)
  0x81, 0x02,             //   Input (Data, Var, Abs)
  0x06, 0x00, 0xFF,       //   Usage Page (Vendor Defined 0xFF00)
  0x09, 0x20,             //   Usage (0x20)         byte 7 bits 2-7: report counter
  0x75, 0x06,             //   Report Size (6)
  0x95, 0x01,             //   Report Count (1)
  0x25, 0x3F,             //   Logical Maximum (63)
  0x81, 0x02,             //   Input (Data, Var, Abs)
  0x05, 0x01,             //   Usage Page (Generic Desktop)
  0x09, 0x33,             //   Usage (Rx)           L2 analog  byte 8
  0x09, 0x34,             //   Usage (Ry)           R2 analog  byte 9
  0x15, 0x00,             //   Logical Minimum (0)
  0x26, 0xFF, 0x00,       //   Logical Maximum (255)
  0x75, 0x08,             //   Report Size (8)
  0x95, 0x02,             //   Report Count (2)
  0x81, 0x02,             //   Input (Data, Var, Abs)
  0x06, 0x00, 0xFF,       //   Usage Page (Vendor Defined 0xFF00)
  0x09, 0x21,             //   Usage (0x21)         bytes 10-63: timestamp, IMU, battery, touchpad
  0x95, 0x36,             //   Report Count (54)
  0x81, 0x02,             //   Input (Data, Var, Abs)

  // ---- Output report 0x05 (rumble, lightbar) ------------------------------------------------------
  0x85, 0x05,             //   Report ID (5)
  0x09, 0x22,             //   Usage (0x22)
  0x95, 0x1F,             //   Report Count (31)
  0x91, 0x02,             //   Output (Data, Var, Abs)

  // ---- Feature reports ----------------------------------------------------------------------------
  0x85, 0x02,             //   Report ID (2)        IMU calibration
  0x09, 0x24,             //   Usage (0x24)
  0x95, 0x24,             //   Report Count (36)
  0xB1, 0x02,             //   Feature (Data, Var, Abs)
  0x85, 0x12,             //   Report ID (0x12)     pairing info: MAC address
  0x09, 0x25,             //   Usage (0x25)
  0x95, 0x0F,             //   Report Count (15)
  0xB1, 0x02,             //   Feature (Data, Var, Abs)
  0x85, 0xA3,             //   Report ID (0xA3)     firmware info
  0x09, 0x26,             //   Usage (0x26)
  0x95, 0x30,             //   Report Count (48)
  0xB1, 0x02,             //   Feature (Data, Var, Abs)

  0xC0,                   // End Collection
};
// clang-format on

constexpr uint32_t kDs4ReportDescriptorSize = sizeof(kDs4ReportDescriptor);

// Byte offsets inside the 64-byte input report 0x01 (report id at offset 0).
enum Ds4InputOffset : uint32_t {
  kDs4OffLeftX = 1,
  kDs4OffLeftY = 2,
  kDs4OffRightX = 3,
  kDs4OffRightY = 4,
  kDs4OffHatButtons = 5,   // bits 0-3 hat (8 = released), 4 square, 5 cross, 6 circle, 7 triangle
  kDs4OffButtons1 = 6,     // L1 R1 L2 R2 share options L3 R3
  kDs4OffButtons2 = 7,     // bit 0 PS, bit 1 touchpad click, bits 2-7 counter
  kDs4OffL2 = 8,
  kDs4OffR2 = 9,
  kDs4OffTimestamp = 10,   // uint16, 5.33 us units
  kDs4OffGyro = 13,        // 3 x int16
  kDs4OffAccel = 19,       // 3 x int16
  kDs4OffStatus = 30,      // battery level (bits 0-3) | cable connected (bit 4)
  kDs4OffTouchCount = 33,
  kDs4OffTouch0 = 35,      // 4 bytes per finger: bit7 = not touching, 7-bit id, 12-bit x, 12-bit y
  kDs4OffTouch1 = 39,
};

constexpr uint8_t kDs4HatReleased = 0x08;

// Neutral input report: sticks centred, hat released, no buttons, triggers up, cable connected and charged,
// both touch points lifted.
inline void InitNeutralInputReport(uint8_t (&report)[kDs4InputReportSize]) {
  for (uint32_t i = 0; i < kDs4InputReportSize; ++i) {
    report[i] = 0;
  }
  report[0] = kDs4InputReportId;
  report[kDs4OffLeftX] = 0x80;
  report[kDs4OffLeftY] = 0x80;
  report[kDs4OffRightX] = 0x80;
  report[kDs4OffRightY] = 0x80;
  report[kDs4OffHatButtons] = kDs4HatReleased;
  report[kDs4OffStatus] = 0x1B; // cable (0x10) | level 11 = full while charging
  report[kDs4OffTouch0] = 0x80;
  report[kDs4OffTouch1] = 0x80;
}

// Feature report 0x02: IMU calibration with unit gains and zero bias, so that SDL/Steam accept the device and
// map the worker's IMU values 1:1. Layout: int16 gyro pitch/yaw/roll bias, then plus/minus references for each
// gyro axis, gyro speed plus/minus, then accel plus/minus per axis (USB layout). Bytes 35..36 stay 0.
inline void FillCalibrationReport(uint8_t (&report)[kDs4CalibrationSize]) {
  for (uint32_t i = 0; i < kDs4CalibrationSize; ++i) {
    report[i] = 0;
  }
  report[0] = kDs4FeatureCalibrationId;
  auto put16 = [&report](uint32_t offset, int16_t value) {
    report[offset] = static_cast<uint8_t>(static_cast<uint16_t>(value) & 0xFF);
    report[offset + 1] = static_cast<uint8_t>(static_cast<uint16_t>(value) >> 8);
  };
  // gyro bias (1, 3, 5) = 0
  for (uint32_t axis = 0; axis < 3; ++axis) {
    put16(7 + axis * 4, 8192);    // gyro plus reference
    put16(9 + axis * 4, -8192);   // gyro minus reference
  }
  put16(19, 540);                 // gyro speed plus  (deg/s scale used by SDL)
  put16(21, 540);                 // gyro speed minus
  for (uint32_t axis = 0; axis < 3; ++axis) {
    put16(23 + axis * 4, 8192);   // accel plus  (1 g)
    put16(25 + axis * 4, -8192);  // accel minus (-1 g)
  }
}

// Feature report 0x12: bytes 1-6 device MAC (little-endian), bytes 7-9 unknown/0, 10-15 host MAC (0).
inline void FillPairingReport(uint8_t (&report)[kDs4PairingSize], const uint8_t (&mac)[6]) {
  for (uint32_t i = 0; i < kDs4PairingSize; ++i) {
    report[i] = 0;
  }
  report[0] = kDs4FeaturePairingId;
  for (uint32_t i = 0; i < 6; ++i) {
    report[1 + i] = mac[i];
  }
}

// Feature report 0xA3: firmware build date/time strings and versions; zeros except the id are accepted.
inline void FillFirmwareReport(uint8_t (&report)[kDs4FirmwareSize]) {
  for (uint32_t i = 0; i < kDs4FirmwareSize; ++i) {
    report[i] = 0;
  }
  report[0] = kDs4FeatureFirmwareId;
  static const char kDate[] = "Oct 28 2026";
  for (uint32_t i = 0; i + 1 < sizeof(kDate); ++i) {
    report[1 + i] = static_cast<uint8_t>(kDate[i]);
  }
}

} // namespace splitplay::pad
