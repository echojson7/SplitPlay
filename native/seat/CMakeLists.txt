# SPDX-License-Identifier: MIT
# splitplay-seat.exe (Stage 2 stub: IPC, input ring, null RDP connector; FreeRDP arrives in Stage 3).

add_executable(splitplay-seat
  src/main.cpp
  src/null_rdp_connector.cpp
  src/seat_app.cpp)
target_link_libraries(splitplay-seat PRIVATE splitplay::common)
