# SPDX-License-Identifier: MIT
# Native tests (plain-assert harness in sp_test.hpp, run by CTest).

function(splitplay_add_test name)
  add_executable(${name} ${ARGN})
  target_link_libraries(${name} PRIVATE splitplay::pipeline splitplay::common)
  target_include_directories(${name} PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  add_test(NAME ${name} COMMAND ${name})
  set_tests_properties(${name} PROPERTIES TIMEOUT 120 LABELS "unit")
endfunction()

# ---- portable ----------------------------------------------------------------------------------
splitplay_add_test(test_ring_layout test_ring_layout.cpp)
splitplay_add_test(test_input_ring test_input_ring.cpp)
splitplay_add_test(test_capture_ring test_capture_ring.cpp)
splitplay_add_test(test_ipc_codec test_ipc_codec.cpp)
splitplay_add_test(test_ipc_client test_ipc_client.cpp)
splitplay_add_test(test_stub_stages test_stub_stages.cpp)

# ---- Windows -------------------------------------------------------------------------------------
if(WIN32)
  splitplay_add_test(test_win_objects test_win_objects.cpp)

  # Plays the service: creates the control pipe, launches a child and drives the protocol.
  add_executable(sp_fake_service fake_service.cpp)
  target_link_libraries(sp_fake_service PRIVATE splitplay::common)
  target_include_directories(sp_fake_service PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")

  add_test(NAME seat_ipc_shutdown
    COMMAND sp_fake_service --child $<TARGET_FILE:splitplay-seat> --role seat --scenario shutdown)
  add_test(NAME seat_ipc_pipe_break
    COMMAND sp_fake_service --child $<TARGET_FILE:splitplay-seat> --role seat --scenario break)
  add_test(NAME stubworker_ipc_shutdown
    COMMAND sp_fake_service --child $<TARGET_FILE:splitplay-stubworker> --role stubworker --scenario shutdown)
  add_test(NAME stubworker_ipc_pipe_break
    COMMAND sp_fake_service --child $<TARGET_FILE:splitplay-stubworker> --role stubworker --scenario break)
  set_tests_properties(seat_ipc_shutdown seat_ipc_pipe_break stubworker_ipc_shutdown stubworker_ipc_pipe_break
    PROPERTIES TIMEOUT 120 LABELS "integration")

  # End-to-end stub pipeline on WARP (works on GPU-less CI runners): producer -> ring -> consumer -> encoder -> UDP.
  add_test(NAME stubworker_self_test
    COMMAND splitplay-stubworker --self-test 300 --adapter warp --fps 120)
  set_tests_properties(stubworker_self_test PROPERTIES TIMEOUT 180 LABELS "integration")

  splitplay_add_test(test_rdsprov test_rdsprov.cpp)
  add_dependencies(test_rdsprov splitplay-rdsprov)
  target_include_directories(test_rdsprov PRIVATE "${PROJECT_SOURCE_DIR}/rdsprov/src")
endif()
