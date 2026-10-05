@echo off
chcp 65001 >nul 2>&1
setlocal
cd /d "%~dp0"

echo.
echo  ============================================================
echo   SFS 1.7 TypeBridge - One Click Tool
echo  ============================================================
echo.
echo   Usage:
echo     - Double click this file, then type/paste your game folder
echo     - Or DRAG your game folder onto this .bat file
echo     - Or: 一键启用.bat --check-only     (only health check)
echo     - Or: 一键启用.bat --uninstall      (restore original files)
echo.

set PY=
if exist "C:\Users\afs\AppData\Local\Programs\Python\Python310\python.exe" set PY=C:\Users\afs\AppData\Local\Programs\Python\Python310\python.exe
if "%PY%"=="" (
  where py >nul 2>&1 && set PY=py
)
if "%PY%"=="" (
  where python >nul 2>&1 && set PY=python
)
if "%PY%"=="" (
  echo  [!] Python not found. Please install Python 3.x and retry.
  echo.
  pause
  exit /b 1
)

"%PY%" "%~dp0enable_bridge.py" %*

echo.
pause
endlocal
