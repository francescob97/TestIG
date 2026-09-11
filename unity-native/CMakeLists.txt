# ============================================================================
#  Plugin nativo per Unity: UnityGpuShare.dll
#
#  Build (da una "x64 Native Tools Command Prompt for VS 2022"):
#
#    cd unity-native
#    cmake -B build -A x64 ^
#          -DUNITY_PLUGIN_API_DIR="C:/Program Files/Unity/Hub/Editor/2022.3.xxf1/Editor/Data/PluginAPI"
#    cmake --build build --config Release
#
#  La DLL viene copiata automaticamente in
#    unity/GpuShareSpike/Assets/Plugins/x86_64/UnityGpuShare.dll
#
#  UNITY_PLUGIN_API_DIR deve puntare alla cartella PluginAPI della TUA
#  installazione di Unity: quegli header (IUnityInterface.h, IUnityGraphics.h,
#  IUnityGraphicsD3D11.h) non sono ridistribuiti in questo repository.
# ============================================================================

cmake_minimum_required(VERSION 3.20)
project(UnityGpuShare LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "Questo plugin usa D3D11: si compila solo su Windows.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(UNITY_PLUGIN_API_DIR "" CACHE PATH
    "Cartella PluginAPI di Unity, es. C:/Program Files/Unity/Hub/Editor/2022.3.xxf1/Editor/Data/PluginAPI")

if(NOT UNITY_PLUGIN_API_DIR OR NOT EXISTS "${UNITY_PLUGIN_API_DIR}/IUnityGraphics.h")
    message(FATAL_ERROR
        "UNITY_PLUGIN_API_DIR non impostata o non valida.\n"
        "Ripassa cmake con -DUNITY_PLUGIN_API_DIR=\"<Unity>/Editor/Data/PluginAPI\"")
endif()

add_library(UnityGpuShare SHARED
    src/UnityPluginEntry.cpp
    src/ChannelConsumer.cpp
    src/MarkerReader.cpp
    src/Exports.h
    src/ChannelConsumer.h
    src/MarkerReader.h
    src/NativeApi.h
)

target_include_directories(UnityGpuShare PRIVATE
    "${UNITY_PLUGIN_API_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    # ShareProtocol.h: la stessa identica copia che include il modulo Unreal.
    "${CMAKE_CURRENT_SOURCE_DIR}/../shared"
)

target_link_libraries(UnityGpuShare PRIVATE d3d11 dxgi dxguid)

target_compile_definitions(UnityGpuShare PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    UNICODE
    _UNICODE
)

if(MSVC)
    target_compile_options(UnityGpuShare PRIVATE /W4 /permissive-)
endif()

# Niente prefisso "lib": Unity cerca esattamente UnityGpuShare.dll.
set_target_properties(UnityGpuShare PROPERTIES PREFIX "" OUTPUT_NAME "UnityGpuShare")

set(UNITY_PLUGINS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../unity/GpuShareSpike/Assets/Plugins/x86_64")

add_custom_command(TARGET UnityGpuShare POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${UNITY_PLUGINS_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:UnityGpuShare>" "${UNITY_PLUGINS_DIR}/UnityGpuShare.dll"
    COMMENT "Copia di UnityGpuShare.dll in Assets/Plugins/x86_64"
)
