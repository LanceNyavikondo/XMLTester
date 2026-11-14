@echo off
setlocal
cd /d "%~dp0"

echo ============================================================
echo  Publishing XMLTester — Linux x64 self-contained binary
echo  EFT Corporation
echo ============================================================
echo.

dotnet publish -c Release -r linux-x64 --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o dist\linux-x64

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Publish failed. Check output above.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo  Success: dist\linux-x64\XMLTester
echo  Copy to Linux target and run:  chmod +x XMLTester  then  ./XMLTester
echo ============================================================
echo.
pause
