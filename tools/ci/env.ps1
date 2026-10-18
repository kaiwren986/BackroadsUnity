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
# --- Added by T0.6 (lane I, CI scripts). Names above are frozen; add new ones below. ---------------
# MAIN_REPO: the main checkout even when this file runs inside a worktree (.git is then a file).
$script:MAIN_REPO = & {
    $g = Join-Path $REPO '.git'
    if (-not (Test-Path -LiteralPath $g -PathType Leaf)) { return $REPO }
    $first = Get-Content -LiteralPath $g -TotalCount 1
    if ($first -notmatch '^gitdir:\s*(.+)$') { return $REPO }
    $gd = $Matches[1].Trim()
    if (-not [IO.Path]::IsPathRooted($gd)) { $gd = Join-Path $REPO $gd }
    $common = $gd
    $cd = Join-Path $gd 'commondir'
    if (Test-Path -LiteralPath $cd) {
        $c = (Get-Content -LiteralPath $cd -TotalCount 1).Trim()
        if (-not [IO.Path]::IsPathRooted($c)) { $c = Join-Path $gd $c }
        $common = $c
    }
    return [IO.Path]::GetFullPath((Join-Path $common '..')).TrimEnd('\')
}
$script:GIT_COMMON = Join-Path $MAIN_REPO '.git'
# LOCKS: machine-wide lock folder (artifacts\ of the MAIN checkout), shared by every worktree.
# BR_LOCKS_DIR overrides it for the CI self-test only.
$script:LOCKS   = Join-Path $MAIN_REPO 'artifacts'
if ($env:BR_LOCKS_DIR) { $script:LOCKS = $env:BR_LOCKS_DIR }
$script:BUILDS  = Join-Path $REPO 'builds'
$script:BACKUP_DEST = 'D:\Backroads-Unity-backups'     # until the user names a drive (QUEUE U21)
$script:LFS_BUDGET_GIB = 6                              # PLAN 1.2
$script:UNITY_SLOTS = 4                                 # PLAN 1.10
$script:WSL_DISTRO = 'Ubuntu-24.04'
$script:POWERSHELL = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $POWERSHELL)) { $script:POWERSHELL = Join-Path $PSHOME 'pwsh.exe' }
New-Item -ItemType Directory -Force $ART | Out-Null
New-Item -ItemType Directory -Force $LOCKS | Out-Null
