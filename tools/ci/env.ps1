# Tool paths for every script in tools\. Dot-source it: . "$PSScriptRoot\env.ps1"
# This is the only place the Unity editor version and tool locations are written down.
$ErrorActionPreference = 'Stop'
$script:REPO    = (Resolve-Path "$PSScriptRoot\..\..").Path
$script:PROJ    = Join-Path $REPO 'Game'
$script:ART     = Join-Path $REPO 'artifacts'
$script:UNITY_VERSION = '6000.3.20f1'
$script:UNITY   = "C:\Program Files\Unity\Hub\Editor\$UNITY_VERSION\Editor\Unity.exe"
$script:UNITY_DATA = "C:\Program Files\Unity\Hub\Editor\$UNITY_VERSION\Editor\Data"
$script:BLENDER = Join-Path $env:LOCALAPPDATA 'Programs\Blender Foundation\Blender 4.5\blender.exe'
$script:NODE    = (Get-Command node -ErrorAction SilentlyContinue).Source
$script:CMAKE   = (Get-Command cmake -ErrorAction SilentlyContinue).Source
$script:EDGE    = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$script:VSWHERE = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$script:ORACLE  = 'D:\Backroads'
$script:ORACLE_COMMIT = '552f7807f4ad56547815c32a5e8407a7c324d556'
$script:ENGINESIM_REF = 'D:\Backroads-ref\engine-sim'
$script:WORKTREES = 'D:\Backroads-Unity-wt'
New-Item -ItemType Directory -Force $ART | Out-Null
