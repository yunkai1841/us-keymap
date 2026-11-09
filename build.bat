@echo off
rem Build UsKeyBridge with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK or Visual Studio is required.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] C# compiler not found: %CSC%
    goto :fail
)
if not exist bin mkdir bin

echo [1/3] Building bin\UsKeyBridge.exe ...
"%CSC%" -nologo -optimize+ -codepage:65001 -platform:x64 -target:winexe -out:bin\UsKeyBridge.exe ^
    -r:System.Windows.Forms.dll -r:System.Drawing.dll src\*.cs
if errorlevel 1 goto :fail

echo [2/3] Building bin\EngineTests.exe ...
"%CSC%" -nologo -codepage:65001 -platform:x64 -target:exe -out:bin\EngineTests.exe ^
    src\KeyMap.cs src\Engine.cs tests\EngineTests.cs
if errorlevel 1 goto :fail

echo [3/3] Running tests ...
bin\EngineTests.exe
if errorlevel 1 goto :fail

echo.
echo Done: bin\UsKeyBridge.exe
if /i not "%~1"=="nopause" pause
exit /b 0

:fail
echo.
echo BUILD FAILED
if /i not "%~1"=="nopause" pause
exit /b 1
