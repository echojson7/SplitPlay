// SPDX-License-Identifier: MIT
// Portable stub stages: test pattern, null bitstream, UDP loopback transport and receiver, input sink, trace stats.
#include <atomic>
#include <chrono>
#include <mutex>
#include <thread>
#include <vector>

#include "sp_test.hpp"
#include "splitplay/common/qpc.hpp"
#include "splitplay/common/trace.hpp"
#include "splitplay/pipeline/null_bitstream.hpp"
#include "splitplay/pipeline/sendinput_sink.hpp"
#include "splitplay/pipeline/test_pattern.hpp"
#include "splitplay/pipeline/udp_loopback_transport.hpp"

using namespace splitplay;
using namespace splitplay::pipeline;
using namespace std::chrono_literals;

namespace {

std::vector<uint8_t> pattern_frame(uint64_t frame_id, uint32_t w, uint32_t h, size_t pitch) {
  std::vector<uint8_t> px(pitch * h, 0xAB);  // padding bytes must not affect the hash
  fill_pattern(frame_id, w, h, px.data(), pitch);
  return px;
}

EncodedFrame encode(uint64_t frame_id, uint32_t w, uint32_t h) {
  const size_t pitch = w * 4u + 64;
  auto px = pattern_frame(frame_id, w, h, pitch);
  EncodedFrame f;
  f.frame_id = frame_id;
  f.keyframe = frame_id == 1;
  f.bitstream = build_null_bitstream(frame_id, w, h, px.data(), pitch, f.keyframe, 8, &f.content_hash);
  return f;
}

}  // namespace

TEST_CASE("test pattern moves and hashes ignore padding") {
  CHECK(pattern_pixel(1, 8, 0, 640, 360) == 0xFFFFFFFFu);   // bar at x = 8
  CHECK(pattern_pixel(2, 8, 0, 640, 360) != 0xFFFFFFFFu);   // moved to x = 16
  CHECK(pattern_pixel(2, 16, 0, 640, 360) == 0xFFFFFFFFu);
  CHECK((pattern_pixel(3, 100, 100, 640, 360) >> 24) == 0xFFu);
  auto a = pattern_frame(5, 64, 32, 64 * 4);
  auto b = pattern_frame(5, 64, 32, 64 * 4 + 32);
  CHECK(hash_pixels(a.data(), 64, 32, 64 * 4) == hash_pixels(b.data(), 64, 32, 64 * 4 + 32));
  CHECK(hash_pixels(a.data(), 64, 32, 64 * 4) == pattern_hash(5, 64, 32));
  CHECK(pattern_hash(5, 64, 32) != pattern_hash(6, 64, 32));
}

TEST_CASE("null bitstream round trip and corruption detection") {
  EncodedFrame f = encode(42, 100, 50);
  CHECK_EQ(f.bitstream.size(), sizeof(NullBitstreamHeader) + 13u * 7u * 4u);
  NullBitstreamInfo info;
  std::string err;
  CHECK_MSG(verify_null_bitstream(f.bitstream.data(), f.bitstream.size(), true, &info, &err), err);
  CHECK_EQ(info.frame_id, 42u);
  CHECK_EQ(info.width, 100u);
  CHECK_EQ(info.height, 50u);
  CHECK(info.content_hash == f.content_hash);

  auto bad = f.bitstream;
  bad[sizeof(NullBitstreamHeader) + 20] ^= 0x01;  // thumbnail pixel
  CHECK(!verify_null_bitstream(bad.data(), bad.size(), true, nullptr, &err));
  bad = f.bitstream;
  bad[40] ^= 0x01;  // content hash
  CHECK(!verify_null_bitstream(bad.data(), bad.size(), true, nullptr, &err));
  CHECK(verify_null_bitstream(bad.data(), bad.size(), false, nullptr, &err));  // structure still fine
  CHECK(!verify_null_bitstream(f.bitstream.data(), f.bitstream.size() - 1, false, nullptr, &err));
  CHECK(!verify_null_bitstream(f.bitstream.data(), 10, false, nullptr, &err));
}

TEST_CASE("udp loopback delivers and verifies frames") {
  std::string err;
  auto rx = UdpLoopbackReceiver::create(&err);
  REQUIRE_MSG(rx != nullptr, err);
  CHECK(rx->port() != 0);
  trace::Recorder rec;
  trace::Tracer tracer("udp-test", &rec);
  auto tx = UdpLoopbackTransport::create({rx->port(), 1200, &tracer}, &err);
  REQUIRE_MSG(tx != nullptr, err);

  std::mutex m;
  std::vector<uint64_t> got;
  std::atomic<int> verified{0}, corrupt{0};
  rx->start([&](UdpLoopbackReceiver::CompletedFrame&& f) {
    std::string e;
    NullBitstreamInfo info;
    if (verify_null_bitstream(f.data.data(), f.data.size(), true, &info, &e) && info.frame_id == f.frame_id) {
      ++verified;
    } else {
      ++corrupt;
    }
    std::lock_guard lock(m);
    got.push_back(f.frame_id);
  });

  constexpr int kFrames = 60;
  for (int i = 1; i <= kFrames; ++i) {
    EncodedFrame f = encode(static_cast<uint64_t>(i), 320, 180);  // ~3.6 KB -> 4 packets
    SendResult r;
    REQUIRE_MSG(tx->send(f, r, &err), err);
    CHECK(r.packets >= 3);
    CHECK(r.first_packet_qpc <= r.last_packet_qpc);
    std::this_thread::sleep_for(1ms);
  }
  const auto deadline = std::chrono::steady_clock::now() + 3s;
  while (verified.load() + corrupt.load() < kFrames && std::chrono::steady_clock::now() < deadline)
    std::this_thread::sleep_for(10ms);
  rx->stop();
  CHECK_EQ(corrupt.load(), 0);
  CHECK_EQ(verified.load(), kFrames);  // loopback with a large receive buffer: no loss expected
  {
    std::lock_guard lock(m);
    for (size_t i = 1; i < got.size(); ++i) CHECK(got[i] > got[i - 1]);
  }
  auto ts = tx->stats();
  CHECK_EQ(ts.frames_sent, static_cast<uint64_t>(kFrames));
  CHECK_EQ(rx->stats().frames_complete, static_cast<uint64_t>(kFrames));
  CHECK_EQ(rec.frames_with_stage(SP_STAGE_FIRST_PACKET), static_cast<size_t>(kFrames));
  CHECK_EQ(rec.frames_with_stage(SP_STAGE_LAST_PACKET), static_cast<size_t>(kFrames));
}

TEST_CASE("receiver rejects foreign datagrams") {
  std::string err;
  auto rx = UdpLoopbackReceiver::create(&err);
  REQUIRE(rx != nullptr);
  std::atomic<int> frames{0};
  rx->start([&](UdpLoopbackReceiver::CompletedFrame&&) { ++frames; });
  auto tx = UdpLoopbackTransport::create({rx->port(), 1200, nullptr}, &err);
  REQUIRE(tx != nullptr);
  // A zero-length frame is one packet with an empty payload and is valid.
  EncodedFrame empty;
  empty.frame_id = 1;
  SendResult r;
  CHECK(tx->send(empty, r, &err));
  CHECK_EQ(r.packets, 1u);
  const auto deadline = std::chrono::steady_clock::now() + 2s;
  while (frames.load() < 1 && std::chrono::steady_clock::now() < deadline) std::this_thread::sleep_for(5ms);
  rx->stop();
  CHECK_EQ(frames.load(), 1);
}

TEST_CASE("trace recorder statistics") {
  trace::Recorder rec(16);
  const uint64_t f = qpc_frequency();
  for (uint64_t id = 1; id <= 10; ++id) {
    rec.stamp(id, SP_STAGE_IDD_PUBLISH, 1000 * f);
    rec.stamp(id, SP_STAGE_WORKER_WAKE, 1000 * f + id * f / 1000);  // id ms later
  }
  auto s = rec.interval(SP_STAGE_IDD_PUBLISH, SP_STAGE_WORKER_WAKE);
  CHECK_EQ(s.count, 10u);
  CHECK(s.min_us > 999 && s.min_us < 1001);
  CHECK(s.max_us > 9999 && s.max_us < 10001);
  CHECK(s.p50_us > 4999 && s.p50_us < 6001);
  CHECK(rec.interval(SP_STAGE_PRESENT, SP_STAGE_LAST_PACKET).count == 0);
  // Capacity 16: old frames are overwritten by newer ones in the same bucket.
  for (uint64_t id = 17; id <= 26; ++id) rec.stamp(id, SP_STAGE_IDD_PUBLISH, 1);
  CHECK(rec.interval(SP_STAGE_IDD_PUBLISH, SP_STAGE_WORKER_WAKE).count == 0);
  CHECK(!trace::format_stats_table(rec.frame_pipeline_summary()).empty());
  trace::Tracer t("desk-1", &rec);
  CHECK_EQ(t.desk_hash(), sp_desk_hash("desk-1"));
}

TEST_CASE("sendinput stub sink counts events") {
  SendInputSink sink;
  SpInputEvent e{};
  e.type = SP_IN_KEY;
  for (int i = 0; i < 5; ++i) CHECK(sink.inject(e));
  e.type = SP_IN_RELEASE_ALL;
  CHECK(sink.inject(e));
  CHECK_EQ(sink.injected(), 6u);
  CHECK_EQ(sink.releases(), 1u);
  CHECK(sink.supports_relative_mouse());
}

SP_TEST_MAIN()
