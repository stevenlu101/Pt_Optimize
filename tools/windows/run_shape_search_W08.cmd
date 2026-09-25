@echo off
rem ============================================================================
rem  W08 shape search, both families, on the local Windows machine (the record platform).
rem  Same driver as the APP's "shape search" button (Core/ShapeSearchDriver, fe7c7e3 and later)
rem  and as the Linux pre-runs. Evidence files land in deliverable\ with a time stamp
rem  (one per family; the sketch re-judge writes its file at the end).
rem  Usage:  tools\windows\run_shape_search_W08.cmd            build, then start the three runs
rem          tools\windows\run_shape_search_W08.cmd nobuild    skip restore/build
rem  Each run gets its own window (close the window = cancel that run). Logs: logs\*.log
rem  Knobs live in _run_one.cmd (SHAPE_LANES 2, SHAPE_SCREEN 8, SHAPE_FINAL 40; SHAPE_MAXDISC unset = program decides).
rem  Do not run the fast test suite on this machine while these run: it rewrites tracked deliverable text.
rem ============================================================================
setlocal
chcp 65001 >nul
cd /d "%~dp0..\.."
if not exist logs mkdir logs
if /i "%~1"=="nobuild" goto run
echo === restore (--disable-parallel) ===
dotnet restore Pt_Optimize.Tests\Pt_Optimize.Tests.csproj --disable-parallel || goto fail
echo === build Release ===
dotnet build Pt_Optimize.Tests\Pt_Optimize.Tests.csproj -c Release --no-restore || goto fail
:run
echo === starting family 0 (no tab holes) ===
start "W08 family 0 - no tab holes" cmd /c tools\windows\_run_one.cmd 0
timeout /t 20 /nobreak >nul
echo === starting family 1 (tab holes, fork pinned at tab mid) ===
start "W08 family 1 - tab holes" cmd /c tools\windows\_run_one.cmd 1
timeout /t 20 /nobreak >nul
echo === starting sketch Y re-judge (decision 103, judge mesh, three gates) ===
start "sketch Y re-judge" cmd /c tools\windows\_run_one.cmd sketch
echo.
echo Three windows started. Progress: type logs\shape_W08_cuts0.log  (or cuts1 / sketchY).
echo Evidence: dir deliverable\*W08*  and  dir deliverable\*103*
endlocal
exit /b 0
:fail
echo BUILD FAILED - nothing started.
endlocal
exit /b 1
