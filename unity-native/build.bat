@echo off

cd /d "%~dp0"

del  ..\unity\GpuShareSpike\Assets\Plugins\x86_64\UnityGpuShare.dll

cmake -B build -A x64 -DUNITY_PLUGIN_API_DIR="C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PluginAPI"

cmake --build build --config Release

pause