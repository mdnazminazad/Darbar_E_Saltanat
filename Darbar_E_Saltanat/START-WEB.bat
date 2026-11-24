@echo off
setlocal
title Darbar_E_Saltanat - local web
cd /d "%~dp0"
echo.
echo  Darbar_E_Saltanat - starting the web portal on this computer
echo  ------------------------------------------------------------

where node >nul 2>nul
if errorlevel 1 (
  echo  Node.js 20 or newer is required. Install the LTS version from https://nodejs.org and run this again.
  pause
  exit /b 1
)
for /f "tokens=1 delims=v." %%a in ('node -v') do set NODEMAJOR=%%a
if %NODEMAJOR% LSS 20 (
  echo  Node.js 20 or newer is required ^(found v%NODEMAJOR%^). Update from https://nodejs.org
  pause
  exit /b 1
)

if not exist "web\server\node_modules" (
  echo  Installing API packages ^(first run only, needs internet^)...
  pushd web\server
  call npm install --no-audit --no-fund
  if errorlevel 1 ( popd & echo  npm install failed for the API. & pause & exit /b 1 )
  popd
)
if not exist "web\client\node_modules" (
  echo  Installing web packages ^(first run only, needs internet^)...
  pushd web\client
  call npm install --no-audit --no-fund
  if errorlevel 1 ( popd & echo  npm install failed for the web client. & pause & exit /b 1 )
  popd
)

echo  Starting the API on http://127.0.0.1:4000 ...
start "Darbar API (close to stop)" cmd /k "cd /d "%~dp0web\server" && npm run dev:sqlite"
timeout /t 6 /nobreak >nul
echo  Starting the web portal on http://localhost:5173 ...
start "Darbar Web (close to stop)" cmd /k "cd /d "%~dp0web\client" && npm run dev"
timeout /t 5 /nobreak >nul
start "" http://localhost:5173

echo.
echo  Opened http://localhost:5173  -  login: admin / ChangeMe@123 ^(you will set a new password^)
echo  Data is in memory only and resets when you close the "Darbar API" window.
echo  The API window also shows a registered desktop Device ID and token for testing sync.
echo  Close the two windows to stop.
echo.
pause
