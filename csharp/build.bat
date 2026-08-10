@echo off
rem ---------------------------------------------------------------
rem  Build JmaMap.exe with the C# compiler that ships with Windows.
rem  No Visual Studio, no .NET SDK, no NuGet, no MSBuild required.
rem  (Comments are ASCII on purpose: cmd.exe uses CP932 here.)
rem ---------------------------------------------------------------
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo ERROR: C# compiler not found.
  echo        .NET Framework 4.x is required ^(bundled with Windows 10/11^).
  pause
  exit /b 1
)

echo Compiler: %CSC%
echo Building JmaMap.exe ...

"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0JmaMap.exe" ^
  /r:System.dll ^
  /r:System.Core.dll ^
  /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll ^
  "%~dp0src\*.cs"

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)

echo.
echo BUILD OK -^> %~dp0JmaMap.exe
pause
