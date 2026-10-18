<#
.SYNOPSIS
  Canon export (PLAN section 1.8.3, T0.7c): oracle snapshot -> headless Edge -> CanonData files, then verify.

.DESCRIPTION
  1. node tools\oracle\make-snapshot.mjs (unless -NoSnapshot)
  2. node tools\oracle\serve-export.mjs --root artifacts\oracle\552f780 --out <Out> --port <Port> (127.0.0.1 only)
  3. msedge.exe --headless=new with a fresh temporary profile at index.html?export=unity; if the page reports
     a failure (for example no WebGL), one retry with --use-angle=swiftshader --enable-unsafe-swiftshader;
     if the page stalls (server exit 6: no request for 2 min), one more try with a fresh profile
  4. waits up to -TimeoutMin minutes for the server to exit, then kills only the Edge processes that use
     this run's profile folder and deletes that folder
  5. node tools\oracle\verify-canon.mjs <Out>: hashes and counts against tools\oracle\expected.json

  -Twice runs two exports and compares them with compare-exports.mjs (manifests equal apart from
  exportedAt, userAgent, timings and reference/shots/*; shots SSIM >= 0.99).
  -Install copies the (first) verified export into CanonData\v1. It refuses to overwrite a CanonData\v1
  whose files differ unless -Force (CanonData\v1 is frozen; changes go through CanonData\CHANGES.md).

.EXAMPLE
  tools\oracle\run-export.ps1 -Twice -Install
#>
param(
  [string]$Out = '',
  [int]$Port = 8131,
  [int]$TimeoutMin = 10,
  [ValidateSet('auto', 'gpu', 'swiftshader')][string]$Renderer = 'auto',
  [string]$Query = '',
  [switch]$NoSnapshot,
  [switch]$Twice,
  [switch]$Install,
  [switch]$Force
)
. "$PSScriptRoot\..\ci\env.ps1"
$ErrorActionPreference = 'Stop'
if (-not $NODE) { throw 'node not found (tools\ci\env.ps1)' }
if (-not (Test-Path $EDGE)) { throw "Edge not found at $EDGE" }

$lock = Get-Content (Join-Path $REPO 'oracle.lock.json') -Raw | ConvertFrom-Json
$short = $lock.commit.Substring(0, 7)
$snap = Join-Path $ART "oracle\$short"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Test-PortFree([int]$p) {
  try { $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse('127.0.0.1'), $p); $l.Start(); $l.Stop(); return $true } catch { return $false }
}

function Stop-EdgeProfile([string]$profileDir) {
  $procs = Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profileDir) }
  foreach ($p in $procs) { try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch { } }
  return @($procs).Count
}

function Invoke-OneExport([string]$outDir, [string]$tag) {
  if (Test-Path $outDir) {
    if ((Get-ChildItem $outDir -Force | Measure-Object).Count -gt 0) { throw "$outDir is not empty" }
  }
  $port = $Port
  while (-not (Test-PortFree $port)) {
    Write-Host "port $port is in use, trying $($port + 1)"
    $port++
    if ($port -gt $Port + 20) { throw 'no free port' }
  }
  $logDir = Join-Path $ART "oracle\logs"
  New-Item -ItemType Directory -Force $logDir | Out-Null
  $modes = @()
  if ($Renderer -eq 'auto') { $modes = @('gpu', 'swiftshader') } else { $modes = @($Renderer) }
  $runs = @()
  foreach ($m in $modes) { $runs += @{ Mode = $m; Attempt = 1 }; $runs += @{ Mode = $m; Attempt = 2 } }
  $skipMode = ''
  $retryStall = $false
  foreach ($run in $runs) {
    $mode = $run.Mode
    if ($mode -eq $skipMode) { continue }
    if ($run.Attempt -eq 2 -and -not $retryStall) { continue }
    $retryStall = $false
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    $srvOut = Join-Path $logDir "$stamp-$tag-$mode-$($run.Attempt)-server.log"
    $srvErr = Join-Path $logDir "$stamp-$tag-$mode-$($run.Attempt)-server.err.log"
    $srvArgs = @("`"$PSScriptRoot\serve-export.mjs`"", '--root', "`"$snap`"", '--out', "`"$outDir`"", '--port', "$port", '--timeout-min', "$TimeoutMin")
    $srv = Start-Process -FilePath $NODE -ArgumentList $srvArgs -PassThru -NoNewWindow -RedirectStandardOutput $srvOut -RedirectStandardError $srvErr
    $null = $srv.Handle  # keep the handle, or ExitCode stays empty after exit (Windows PowerShell 5.1)
    $ok = $false
    for ($i = 0; $i -lt 50; $i++) {
      Start-Sleep -Milliseconds 200
      if ($srv.HasExited) { break }
      if (-not (Test-PortFree $port)) { $ok = $true; break }
    }
    if (-not $ok) { throw "export server did not start (see $srvErr)" }

    $profileDir = Join-Path $ART "oracle\edge-profile-$stamp-$tag-$mode-$($run.Attempt)"
    New-Item -ItemType Directory -Force $profileDir | Out-Null
    $url = "http://127.0.0.1:$port/index.html?export=unity$Query"
    $edgeArgs = @('--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--disable-sync',
      '--disable-background-networking', '--disable-component-update', '--no-pings', '--disable-default-apps',
      '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows',
      "--user-data-dir=`"$profileDir`"", '--window-size=1600,900', '--force-device-scale-factor=1')
    if ($mode -eq 'swiftshader') { $edgeArgs += @('--use-angle=swiftshader', '--enable-unsafe-swiftshader') }
    $edgeArgs += "`"$url`""
    Write-Host "[$tag/$mode] Edge headless -> $url"
    $null = Start-Process -FilePath $EDGE -ArgumentList $edgeArgs -PassThru
    $deadline = (Get-Date).AddMinutes($TimeoutMin)
    while (-not $srv.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    if (-not $srv.HasExited) { Stop-Process -Id $srv.Id -Force; $code = 124 } else { $srv.WaitForExit(); $code = $srv.ExitCode }
    $killed = Stop-EdgeProfile $profileDir
    Start-Sleep -Milliseconds 500
    $null = Stop-EdgeProfile $profileDir
    try { Remove-Item -Recurse -Force $profileDir -ErrorAction Stop } catch { Start-Sleep -Seconds 2; Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue }
    Get-Content $srvOut -Tail 5 | ForEach-Object { Write-Host "  $_" }
    Write-Host "[$tag/$mode] server exit $code; stopped $killed Edge processes"
    if ($code -eq 0) { return @{ Out = $outDir; Mode = $mode; Port = $port } }
    if ($code -eq 6 -and $run.Attempt -eq 1) { Write-Host "[$tag/$mode] the page stalled; one more try with a fresh profile"; $retryStall = $true; continue }
    if ($code -ne 3 -and $code -ne 6) { throw "export $tag failed with server exit code $code (logs: $srvOut)" }
    $skipMode = $mode
    Write-Host "[$tag/$mode] the page reported a failure (exit $code); $(if ($mode -ne $modes[-1]) { 'retrying with the next renderer' } else { 'no fallback left' })"
  }
  throw "export $tag failed with every renderer; fallbacks: the desktop app's browser pane, or tools\oracle\Export-Canon.bat"
}

if (-not $NoSnapshot) {
  & $NODE "$PSScriptRoot\make-snapshot.mjs"
  if ($LASTEXITCODE -ne 0) { throw 'make-snapshot failed' }
}
if (-not $Out) { $Out = Join-Path $ART "oracle\export-$stamp\v1" }
$Out = [System.IO.Path]::GetFullPath($Out)

$first = Invoke-OneExport $Out 'a'
& $NODE "$PSScriptRoot\verify-canon.mjs" $first.Out --report (Join-Path (Split-Path $first.Out -Parent) 'verify-a.json')
$verifyCode = $LASTEXITCODE
if ($verifyCode -ne 0) { Write-Host "verify-canon FAILED for $($first.Out)"; exit 1 }

if ($Twice) {
  $second = Join-Path (Split-Path $Out -Parent) 'v1-second'
  $b = Invoke-OneExport $second 'b'
  & $NODE "$PSScriptRoot\verify-canon.mjs" $b.Out --report (Join-Path (Split-Path $Out -Parent) 'verify-b.json')
  if ($LASTEXITCODE -ne 0) { Write-Host "verify-canon FAILED for $($b.Out)"; exit 1 }
  & $NODE "$PSScriptRoot\compare-exports.mjs" $first.Out $b.Out --report (Join-Path (Split-Path $Out -Parent) 'compare.json')
  if ($LASTEXITCODE -ne 0) { Write-Host 'the two exports differ'; exit 1 }
}

if ($Install) {
  $dest = Join-Path $REPO 'CanonData\v1'
  if ((Test-Path (Join-Path $dest 'manifest.json')) -and -not $Force) {
    & $NODE "$PSScriptRoot\compare-exports.mjs" $dest $first.Out --files-only
    if ($LASTEXITCODE -ne 0) { throw 'CanonData\v1 exists and differs from this export; it is frozen (use -Force only with an approved CanonData\CHANGES.md entry)' }
  }
  if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
  New-Item -ItemType Directory -Force $dest | Out-Null
  Copy-Item -Recurse -Force (Join-Path $first.Out '*') $dest
  & $NODE "$PSScriptRoot\verify-canon.mjs" $dest
  if ($LASTEXITCODE -ne 0) { throw 'installed CanonData\v1 does not verify' }
  Write-Host "installed -> $dest"
}
Write-Host "export OK: $($first.Out) (renderer: $($first.Mode))"
exit 0
