@echo off
setlocal
cd /d "%~dp0"

set VERSION=0.9.0
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo HATA: .NET Framework C# derleyicisi bulunamadi.
  exit /b 1
)

if not exist dist mkdir dist

rem Exe dosya simgesi: assets\ModbusScan.ico (tools\MakeIcon.ps1 uretir, depoda durur).
set ICON=assets\ModbusScan.ico
set ICONFLAG=
if not exist "%ICON%" (
  echo Simge yok: tools\MakeIcon.ps1 ile uretiliyor...
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\MakeIcon.ps1
)
if exist "%ICON%" (set ICONFLAG=/win32icon:%ICON%) else (echo UYARI: simge uretilemedi, varsayilan ikonla derleniyor.)

echo [1/3] ModbusScan.exe derleniyor...
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu %ICONFLAG% ^
  /reference:System.dll /reference:System.Management.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /out:dist\ModbusScan.exe ^
  src\ModbusTcp.cs src\ModbusRtu.cs src\Updater.cs src\WriteDialog.cs src\MainForm.cs src\Program.cs
if errorlevel 1 exit /b 1

echo [2/3] SelfTest.exe (kendi kendine test) derleniyor...
"%CSC%" /nologo /target:exe /optimize+ ^
  /reference:System.dll /reference:System.Management.dll ^
  /out:dist\SelfTest.exe ^
  src\ModbusTcp.cs src\ModbusRtu.cs src\Updater.cs tools\SelfTest.cs
if errorlevel 1 exit /b 1

echo [3/3] Probe.exe (test araci) derleniyor...
"%CSC%" /nologo /target:exe /optimize+ ^
  /reference:System.dll /reference:System.Management.dll ^
  /out:dist\Probe.exe ^
  src\ModbusTcp.cs src\ModbusRtu.cs tools\Probe.cs
if errorlevel 1 exit /b 1

echo Bitti: dist\ModbusScan.exe
endlocal
