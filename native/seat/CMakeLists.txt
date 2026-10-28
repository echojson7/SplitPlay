# SPDX-License-Identifier: MIT
# splitplay-seat.exe: IPC, input ring and the desk's RDP-loopback session (FreeRDP 3 connector, or the null connector
# when SPLITPLAY_WITH_FREERDP is off).

add_executable(splitplay-seat
  src/main.cpp
  src/null_rdp_connector.cpp
  src/seat_app.cpp
  src/wts_session.cpp)
# No wtsapi32 import library: wts_session.cpp resolves it at run time (WinPR defines the same function names).
target_link_libraries(splitplay-seat PRIVATE splitplay::common)

if(SPLITPLAY_WITH_FREERDP)
  target_sources(splitplay-seat PRIVATE src/freerdp_connector.cpp)
  target_compile_definitions(splitplay-seat PRIVATE SPLITPLAY_HAVE_FREERDP=1)
  target_link_libraries(splitplay-seat PRIVATE freerdp-client freerdp winpr)
endif()
