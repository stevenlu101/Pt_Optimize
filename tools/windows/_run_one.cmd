@echo off
rem Helper for run_shape_search_W08.cmd. Arg 1: 0 = family without tab holes, 1 = family with tab holes, sketch = sketch Y re-judge.
rem Runs in its own window; env vars below are the knobs read by Pt_Optimize.Tests\R48ShapeSearchRunTests.cs.
cd /d "%~dp0..\.."
set SHAPE_LANES=2
set SHAPE_SCREEN=8
set SHAPE_FINAL=40
set SHAPE_PARALLEL=1
set SHAPE_SKIPGROW=1
set SHAPE_REUSE=1
set SHAPE_SHOULDER=1
set SHAPE_CONTENTION=two families plus sketch re-judge on one Windows machine (run_shape_search_W08.cmd)
if /i "%~1"=="sketch" goto sketch
set SHAPE_CUTS=%~1
echo family SHAPE_CUTS=%SHAPE_CUTS%  log: logs\shape_W08_cuts%SHAPE_CUTS%.log
dotnet test Pt_Optimize.Tests\Pt_Optimize.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~R48ShapeSearchRunTests&DisplayName~W08" -- xUnit.MaxParallelThreads=1 > logs\shape_W08_cuts%SHAPE_CUTS%.log 2>&1
echo finished, exit code %ERRORLEVEL%. Evidence: dir deliverable\*W08*
pause
exit /b %ERRORLEVEL%
:sketch
echo sketch Y re-judge  log: logs\sketchY.log
dotnet test Pt_Optimize.Tests\Pt_Optimize.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~R48SketchYRejudgeTests" -- xUnit.MaxParallelThreads=1 > logs\sketchY.log 2>&1
echo finished, exit code %ERRORLEVEL%. Evidence: dir deliverable\*103*
pause
exit /b %ERRORLEVEL%
