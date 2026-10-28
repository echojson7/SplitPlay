# SPDX-License-Identifier: MIT
# Common compile settings shared by every SplitPlay native target.

add_library(splitplay_build_options INTERFACE)
add_library(splitplay::build_options ALIAS splitplay_build_options)

if(MSVC)
  target_compile_options(splitplay_build_options INTERFACE
    /W4 /permissive- /utf-8 /Zc:__cplusplus /EHsc /MP)
  if(SPLITPLAY_WARNINGS_AS_ERRORS)
    target_compile_options(splitplay_build_options INTERFACE /WX)
  endif()
  # Release builds keep PDBs so crash dumps from the field can be symbolised.
  target_compile_options(splitplay_build_options INTERFACE $<$<CONFIG:Release>:/Z7>)
  target_link_options(splitplay_build_options INTERFACE
    "$<$<CONFIG:Release>:/DEBUG>" "$<$<CONFIG:Release>:/OPT:REF>" "$<$<CONFIG:Release>:/OPT:ICF>")
else()
  target_compile_options(splitplay_build_options INTERFACE -Wall -Wextra -Wno-missing-field-initializers)
  if(SPLITPLAY_WARNINGS_AS_ERRORS)
    target_compile_options(splitplay_build_options INTERFACE -Werror)
  endif()
endif()

if(WIN32)
  target_compile_definitions(splitplay_build_options INTERFACE
    WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE _WIN32_WINNT=0x0A00 WINVER=0x0A00)
  if(MINGW)
    # Self-contained binaries when cross-compiling for local checks (no MinGW runtime DLLs needed under Wine).
    target_link_options(splitplay_build_options INTERFACE -static)
  endif()
else()
  find_package(Threads REQUIRED)
  target_link_libraries(splitplay_build_options INTERFACE Threads::Threads)
endif()
