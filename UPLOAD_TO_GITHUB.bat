@echo off
setlocal
title Darbar_E_Saltanat - upload to GitHub
cd /d "%~dp0"
set "REPO=https://github.com/mdnazminazad/Darbar_E_Saltanat.git"

echo.
echo  Uploading this project to %REPO%
echo  ---------------------------------------------------------

where git >nul 2>nul
if errorlevel 1 (
  echo  Git is not installed. Get it from https://git-scm.com/download/win and run this file again.
  pause
  exit /b 1
)

if not exist ".git" git init
git config user.name >nul 2>nul
if errorlevel 1 git config user.name "mdnazminazad"
git config user.email >nul 2>nul
if errorlevel 1 git config user.email "mdnazminazad@users.noreply.github.com"

git add -A
git commit -m "Darbar_E_Saltanat: desktop POS, API and admin portal"
git branch -M main
git remote remove origin >nul 2>nul
git remote add origin %REPO%

echo.
echo  Pushing. If a window asks you to sign in to GitHub, sign in as mdnazminazad and approve.
git push -u origin main
if errorlevel 1 (
  echo.
  echo  The upload did not finish. Check that the repository exists on GitHub ^(empty, no README^)
  echo  and that you signed in as mdnazminazad, then run this file again.
  pause
  exit /b 1
)

echo.
echo  Done: https://github.com/mdnazminazad/Darbar_E_Saltanat
pause
