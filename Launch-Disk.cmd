@echo off
setlocal
set "DISK_APP=%~dp0Binaries\Edirstat.App\0.3.0\Edirstat.App.exe"
if not exist "%DISK_APP%" (
    echo First publish the disk app with: python build.py --target disk
    pause
    exit /b 1
)
start "" "%DISK_APP%"
