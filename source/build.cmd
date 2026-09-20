@echo off
setlocal
cd /d "%~dp0"
set "ERSCSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%ERSCSC%" (
  echo The 64-bit .NET Framework compiler was not found.
  exit /b 1
)
"%ERSCSC%" /nologo /platform:x64 /optimize+ /target:winexe /out:"..\F1 ERS Manager.exe" /win32manifest:app.manifest /win32icon:app.ico /reference:System.Windows.Forms.dll /reference:System.Drawing.dll *.cs
if errorlevel 1 exit /b 1
"%ERSCSC%" /nologo /platform:x64 /optimize+ /target:exe /out:F1ERSManager.Checks.exe /win32manifest:app.manifest /win32icon:app.ico /reference:System.Windows.Forms.dll /reference:System.Drawing.dll *.cs
if errorlevel 1 exit /b 1
F1ERSManager.Checks.exe --self-test
if errorlevel 1 exit /b 1
del /q F1ERSManager.Checks.exe
echo Build and local checks passed. Start "..\F1 ERS Manager.exe"
