@echo off
rem Builds portable\FastCopy.exe from src\ with the C# compiler that ships with
rem Windows (.NET Framework 4.x). Nothing to install.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo C# compiler not found. FastCopy needs .NET Framework 4.x, which is built into Windows 10 and 11.
    exit /b 1
)

if not exist portable mkdir portable
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
    /out:portable\FastCopy.exe ^
    /win32icon:src\FastCopy.ico ^
    /win32manifest:src\app.manifest ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    src\FastCopy.cs
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built portable\FastCopy.exe
