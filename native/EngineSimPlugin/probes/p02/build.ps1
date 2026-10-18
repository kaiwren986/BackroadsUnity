# Reproduces the headless engine-sim probe build (no network, no flex/bison, no Boost, no delta-studio).
# Usage:  powershell -ExecutionPolicy Bypass -File D:\Backroads-ref\probe\build.ps1 [-FastMath]
param([switch]$FastMath)

$probe = 'D:\Backroads-ref\probe'
$build = if ($FastMath) { "$probe\build-fastmath" } else { "$probe\build" }
$fast  = if ($FastMath) { 'ON' } else { 'OFF' }

cmake -S $probe -B $build -G "Visual Studio 17 2022" -A x64 "-DPROBE_FAST_MATH=$fast"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cmake --build $build --config Release --target engine-sim probe
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Built $build\Release\engine-sim.lib and $build\Release\probe.exe"
Write-Host "Run:   $build\Release\probe.exe            (full, ~6 min)"
Write-Host "       $build\Release\probe.exe --quick --parts AB"
