@echo off
rem Builds FringeFree.exe with the C# compiler that ships with Windows.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0FringeFree.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%~dp0FringeFree.cs"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
echo Built FringeFree.exe
pause
