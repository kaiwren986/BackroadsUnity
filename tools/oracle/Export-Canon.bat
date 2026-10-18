@echo off
rem Canon export, manual fallback (PLAN 1.8.3): a normal Edge window instead of headless Edge.
rem Double-click it. It makes the oracle snapshot, starts the receiver on 127.0.0.1:8131, opens Edge on
rem the export page with a fresh temporary profile, waits until the page has sent everything, then
rem verifies the export. Nothing is installed and D:\Backroads is only read (git archive).
rem The result lands in artifacts\oracle\manual-TIME\v1. To make it the canon, run
rem tools\oracle\run-export.ps1 -Install (CanonData\v1 is frozen once committed).
setlocal
cd /d "%~dp0..\.."
set "EDGE=C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set STAMP=%%i
set "OUT=%CD%\artifacts\oracle\manual-%STAMP%\v1"
set "PROFILE=%CD%\artifacts\oracle\edge-profile-manual-%STAMP%"
echo Making the oracle snapshot...
node tools\oracle\make-snapshot.mjs || goto fail
echo Exporting. An Edge window opens; leave it alone until this window says the export is done.
node tools\oracle\serve-export.mjs --root artifacts\oracle\552f780 --out "%OUT%" --port 8131 --timeout-min 10 --open "%EDGE%" --profile "%PROFILE%" || goto fail
echo Verifying...
node tools\oracle\verify-canon.mjs "%OUT%" || goto fail
echo.
echo Export done and verified: %OUT%
echo You can close the Edge window now.
pause
exit /b 0
:fail
echo.
echo The export FAILED. The messages above say why.
pause
exit /b 1
