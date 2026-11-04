@echo off
setlocal
cd /d "%~dp0"

set VERSION=0.1.0
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo HATA: .NET Framework C# derleyicisi bulunamadi.
  exit /b 1
)

if not exist dist mkdir dist

echo [1/2] ModbusScan.exe derleniyor...
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /out:dist\ModbusScan.exe ^
  src\ModbusTcp.cs src\MainForm.cs src\Program.cs
if errorlevel 1 exit /b 1

echo [2/2] Probe.exe ^(test araci^) derleniyor...
"%CSC%" /nologo /target:exe /optimize+ ^
  /reference:System.dll ^
  /out:dist\Probe.exe ^
  src\ModbusTcp.cs tools\Probe.cs

echo Bitti: dist\ModbusScan.exe
endlocal
