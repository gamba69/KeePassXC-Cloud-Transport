@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "NO_PAUSE=0"
for %%A in (%*) do if /I "%%~A"=="-NoPause" set "NO_PAUSE=1"
where dotnet >nul 2>&1
if errorlevel 1 (
  >&2 echo [FAIL] Build / .NET 10 SDK is required
  set "RESULT=1"
  goto finish
)
pushd "%ROOT%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\build.ps1" %*
set "RESULT=%ERRORLEVEL%"
popd
:finish
if "%RESULT%"=="0" (echo [PASS] Build) else (>&2 echo [FAIL] Build / error %RESULT%)
if "%NO_PAUSE%"=="0" pause
exit /b %RESULT%
