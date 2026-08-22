cmake_minimum_required(VERSION 3.20)
project(xmaxsvc VERSION 0.1.0 LANGUAGES CXX)

set(ENV{https_proxy} "http://127.0.0.1:10808")
set(ENV{HTTPS_PROXY} "http://127.0.0.1:10808")

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Binary name follows platform convention
if(WIN32)
    set(BACKEND_TARGET xmaxsvc)
else()
    set(BACKEND_TARGET xmaxd)
endif()

# Dependencies via FetchContent
include(FetchContent)

# nlohmann/json (header-only)
FetchContent_Declare(
    json
    GIT_REPOSITORY https://github.com/nlohmann/json.git
    GIT_TAG v3.11.3
)

# Google Test
FetchContent_Declare(
    googletest
    GIT_REPOSITORY https://github.com/google/googletest.git
    GIT_TAG v1.14.0
)

# AMD ADLX SDK (Windows only - GPU telemetry)
# Strategy: Use local copies from backend/lib/ADLX/ if available, fall back to FetchContent
if(WIN32)
    set(ADLX_LOCAL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/lib/ADLX")
    set(ADLX_REQUIRED_FILES
        "ADLXHelper/Windows/Cpp/ADLXHelper.cpp"
        "Platform/Windows/WinAPIs.cpp"
        "Include/ADLXDefines.h"
    )
    set(ADLX_LOCAL_AVAILABLE TRUE)

    foreach(file ${ADLX_REQUIRED_FILES})
        if(NOT EXISTS "${ADLX_LOCAL_DIR}/${file}")
            set(ADLX_LOCAL_AVAILABLE FALSE)
            message(STATUS "ADLX: Local file missing: ${file}")
        endif()
    endforeach()

    if(NOT ADLX_LOCAL_AVAILABLE)
        message(STATUS "ADLX: Falling back to FetchContent download")
        FetchContent_Declare(
            adlx
            GIT_REPOSITORY https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.git
            GIT_TAG main
        )
    endif()
endif()

# PawnIO binary modules (Windows only - SMU/LPC kernel drivers)
# Strategy: Use local copies from backend/lib/PawnIO/ if available, fall back to FetchContent
if(WIN32)
    set(PAWNIO_LOCAL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/lib/PawnIO")
    set(PAWNIO_FILES "RyzenSMU.bin" "LpcIO.bin")
    set(PAWNIO_LOCAL_AVAILABLE TRUE)

    foreach(file ${PAWNIO_FILES})
        if(NOT EXISTS "${PAWNIO_LOCAL_DIR}/${file}")
            set(PAWNIO_LOCAL_AVAILABLE FALSE)
            message(STATUS "PawnIO: Local file missing: ${file}")
        endif()
    endforeach()

    if(NOT PAWNIO_LOCAL_AVAILABLE)
        message(STATUS "PawnIO: Falling back to FetchContent download")
        FetchContent_Declare(
            pawnio_modules
            GIT_REPOSITORY https://github.com/namazso/PawnIO.Modules.git
            GIT_TAG main
        )
    endif()
endif()

FetchContent_MakeAvailable(json googletest)
if(WIN32)
    if(NOT ADLX_LOCAL_AVAILABLE)
        FetchContent_MakeAvailable(adlx)
    endif()
    if(NOT PAWNIO_LOCAL_AVAILABLE)
        FetchContent_MakeAvailable(pawnio_modules)
    endif()
endif()

# Main executable
add_executable(${BACKEND_TARGET}
    src/main.cpp
    src/logger.cpp
    src/protocol.cpp
    src/config.cpp
    src/profiles.cpp
    src/fan.cpp
    src/tdp.cpp
    src/metrics.cpp
    src/power.cpp
    src/adaptive.cpp
    src/button.cpp
    src/transport.cpp
    src/process.cpp
    src/tray.cpp
)

# Platform-specific sources
if(WIN32)
    # Set ADLX source directory (local or FetchContent)
    if(ADLX_LOCAL_AVAILABLE)
        set(ADLX_SDK_DIR "${ADLX_LOCAL_DIR}")
        message(STATUS "ADLX: Using local SDK from ${ADLX_SDK_DIR}")
    else()
        set(ADLX_SDK_DIR "${adlx_SOURCE_DIR}/SDK")
        message(STATUS "ADLX: Using downloaded SDK from ${ADLX_SDK_DIR}")
    endif()

    # Generate .rc file with absolute path to icon (resource compiler runs from build dir)
    configure_file(
        "${CMAKE_CURRENT_SOURCE_DIR}/xmaxsvc.rc.in"
        "${CMAKE_CURRENT_BINARY_DIR}/xmaxsvc.rc"
        @ONLY
    )

    target_sources(${BACKEND_TARGET} PRIVATE
        src/platform/platform_win32.cpp
        ${ADLX_SDK_DIR}/ADLXHelper/Windows/Cpp/ADLXHelper.cpp
        ${ADLX_SDK_DIR}/Platform/Windows/WinAPIs.cpp
        "${CMAKE_CURRENT_BINARY_DIR}/xmaxsvc.rc"
    )
    target_include_directories(${BACKEND_TARGET} PRIVATE
        ${ADLX_SDK_DIR}/Include
        ${ADLX_SDK_DIR}/ADLXHelper/Windows/Cpp
    )
    target_link_libraries(${BACKEND_TARGET} PRIVATE
        nlohmann_json::nlohmann_json
        ole32
        oleaut32
        wbemuuid
        taskschd
        gdiplus
        dxgi
    )

    # Copy PawnIO binary modules to output directory (post-build for multi-config generators)
    if(PAWNIO_LOCAL_AVAILABLE)
        set(PAWNIO_SOURCE_DIR "${PAWNIO_LOCAL_DIR}")
        message(STATUS "PawnIO: Using local binaries from ${PAWNIO_SOURCE_DIR}")
    else()
        set(PAWNIO_SOURCE_DIR "${pawnio_modules_SOURCE_DIR}")
        message(STATUS "PawnIO: Using downloaded binaries from ${PAWNIO_SOURCE_DIR}")
    endif()

    add_custom_command(TARGET ${BACKEND_TARGET} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "$<TARGET_FILE_DIR:${BACKEND_TARGET}>/lib"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${PAWNIO_SOURCE_DIR}/RyzenSMU.bin"
            "${PAWNIO_SOURCE_DIR}/LpcIO.bin"
            "$<TARGET_FILE_DIR:${BACKEND_TARGET}>/lib/"
        COMMENT "Copying PawnIO binaries to output directory"
    )

    # Copy shared assets (logo.png for tray icon)
    add_custom_command(TARGET ${BACKEND_TARGET} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "$<TARGET_FILE_DIR:${BACKEND_TARGET}>/assets"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${CMAKE_SOURCE_DIR}/../shared/assets/logo.png"
            "$<TARGET_FILE_DIR:${BACKEND_TARGET}>/assets/logo.png"
        COMMENT "Copying shared assets to output directory"
    )
elseif(UNIX)
    target_sources(${BACKEND_TARGET} PRIVATE
        src/platform/platform_linux.cpp
    )
    target_link_libraries(${BACKEND_TARGET} PRIVATE
        nlohmann_json::nlohmann_json
    )
endif()

# Tests
enable_testing()

add_executable(${BACKEND_TARGET}_tests
    tests/test_main.cpp
    src/logger.cpp
    src/protocol.cpp
    src/config.cpp
    src/profiles.cpp
    src/fan.cpp
    src/tdp.cpp
    src/metrics.cpp
    src/power.cpp
    src/adaptive.cpp
    src/button.cpp
    src/transport.cpp
    src/process.cpp
    src/tray.cpp
)

# Add ADLX sources and includes for tests on Windows
if(WIN32)
    target_sources(${BACKEND_TARGET}_tests PRIVATE
        ${ADLX_SDK_DIR}/ADLXHelper/Windows/Cpp/ADLXHelper.cpp
        ${ADLX_SDK_DIR}/Platform/Windows/WinAPIs.cpp
    )
    target_include_directories(${BACKEND_TARGET}_tests PRIVATE
        ${ADLX_SDK_DIR}/Include
        ${ADLX_SDK_DIR}/ADLXHelper/Windows/Cpp
    )
endif()

target_link_libraries(${BACKEND_TARGET}_tests PRIVATE
    GTest::gtest_main
    nlohmann_json::nlohmann_json
)

include(GoogleTest)
gtest_discover_tests(${BACKEND_TARGET}_tests)
