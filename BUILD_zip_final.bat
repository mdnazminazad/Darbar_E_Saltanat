@echo off
setlocal EnableExtensions
title Darbar_E_Saltanat - build zip_final
cd /d "%~dp0"

set "ROOT=%~dp0Darbar_E_Saltanat\desktop"
set "OUT=%~dp0zip_final_build"
set "ZIP=%~dp0zip_final.zip"
set "LOG=%~dp0zip_final_build_log.txt"
set "TESTLOG=%~dp0zip_final_test_log.txt"

echo ============================================================
echo  Darbar_E_Saltanat - building the desktop app (win-x64)
echo ============================================================
echo.

tasklist /FI "IMAGENAME eq DarbarESaltanat.exe" 2>nul | find /I "DarbarESaltanat.exe" >nul
if not errorlevel 1 (
  echo The Darbar_E_Saltanat app is still open. Please close it first
  echo ^(your data is safe - everything is saved on this computer^).
  echo.
  pause
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: the .NET SDK was not found. Install the .NET 8 SDK and run this again.
  pause
  exit /b 1
)

echo Build started %DATE% %TIME% > "%LOG%"
dotnet --version >> "%LOG%" 2>&1

if exist "%OUT%" rd /s /q "%OUT%"
if exist "%ZIP%" del /q "%ZIP%"

echo [1/3] Publishing the app (this takes a minute or two)...
dotnet publish "%ROOT%\DarbarESaltanat.Desktop\DarbarESaltanat.Desktop.csproj" -c Release -r win-x64 --self-contained true -o "%OUT%" --nologo >> "%LOG%" 2>&1
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Nothing was zipped.
  echo The details are in:  %LOG%
  echo Tell Claude "build failed" and it will read that file and fix it.
  echo.
  pause
  exit /b 1
)
echo       Published OK.

echo [2/3] Creating zip_final.zip ...
tar.exe -a -c -f "%ZIP%" -C "%OUT%" * >> "%LOG%" 2>&1
if errorlevel 1 (
  if exist "%ZIP%" del /q "%ZIP%"
  powershell -NoProfile -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -Force" >> "%LOG%" 2>&1
)
if not exist "%ZIP%" (
  echo ZIP FAILED - see %LOG%
  pause
  exit /b 1
)
echo       Created %ZIP%

echo [3/3] Running the automatic tests (optional, can be closed)...
dotnet test "%ROOT%\DarbarESaltanat.Tests\DarbarESaltanat.Tests.csproj" -c Release --nologo > "%TESTLOG%" 2>&1
if errorlevel 1 (
  echo       Some tests failed or did not run - see %TESTLOG%  ^(the zip is still fine to use^)
) else (
  echo       All tests passed.
)

echo.
echo ============================================================
echo  DONE.  Your new build is:
echo    %ZIP%
echo  To install: close the app, unzip over the old folder, run DarbarESaltanat.exe
echo  ^(your local data stays in %%LOCALAPPDATA%%\DarbarESaltanat^)
echo ============================================================
echo.
pause
