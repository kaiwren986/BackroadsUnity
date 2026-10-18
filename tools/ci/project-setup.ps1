# Lane I helper (T0.5/T0.8): re-exports the axis canary with Blender, then runs ProjectSetup.Apply in batchmode
# (imports the project, writes every PLAN 1.3 setting, the runtime UI assets and the TestYard scene).
# Exit code: Unity's (0 = all ProjectSetup checks pass).
[CmdletBinding()]
param(
    [switch]$SkipCanary,
    [string]$Log = ''
)
. "$PSScriptRoot\env.ps1"
$ErrorActionPreference = 'Stop'
if (Test-Path (Join-Path $ART 'machine.lock')) { Write-Host 'machine.lock exists: a perf gate is running'; exit 3 }
if (-not $Log) { $Log = Join-Path $ART 'project-setup.log' }
$sw = [Diagnostics.Stopwatch]::StartNew()

if (-not $SkipCanary) {
    $fbx = Join-Path $REPO 'art\canary\CANARY.fbx'
    & $BLENDER --background --factory-startup --python (Join-Path $REPO 'art\pipeline\br_canary.py') -- --out $fbx | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "br_canary.py failed ($LASTEXITCODE)"; exit 1 }
    $dst = Join-Path $PROJ 'Assets\Art\Canary'
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item $fbx (Join-Path $dst 'CANARY.fbx') -Force
}

$args_ = @('-batchmode', '-nographics', '-quit', '-projectPath', "`"$PROJ`"",
           '-executeMethod', 'Backroads.Build.Editor.ProjectSetup.Apply', '-logFile', "`"$Log`"")
$p = Start-Process -FilePath $UNITY -ArgumentList $args_ -PassThru -NoNewWindow
$null = $p.Handle   # keeps ExitCode readable after exit
$p.WaitForExit()
$code = $p.ExitCode
Get-Content $Log | Select-String -Pattern '\[BR\]|error CS|Exception' | Select-Object -Last 40 | ForEach-Object { $_.Line }
Write-Host ("project-setup: exit {0} in {1:F0} s (log {2})" -f $code, $sw.Elapsed.TotalSeconds, $Log)
exit $code
