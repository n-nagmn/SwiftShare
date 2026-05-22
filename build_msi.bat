@echo off
set WIX_PATH="C:\Program Files (x86)\WiX Toolset v3.11\bin"
if not exist %WIX_PATH% (
    echo WiX Toolset not found at %WIX_PATH%. Please install it or update the path in this script.
    pause
    exit /b
)

echo Compiling...
%WIX_PATH%\candle.exe SwiftShare.wxs
if errorlevel 1 exit /b

echo Linking...
%WIX_PATH%\light.exe SwiftShare.wixobj -o SwiftShare.msi
if errorlevel 1 exit /b

echo Done! SwiftShare.msi created.
pause
