@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
if not defined VCToolsInstallDir (
  for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
  if not defined VSROOT exit /b 2
  call "!VSROOT!\VC\Auxiliary\Build\vcvars64.bat" >nul
  if errorlevel 1 exit /b 2
)
if not exist build mkdir build
cl /nologo /O2 /W3 /MT /Ivendor\include /c vendor\src\buffer.c vendor\src\hook.c vendor\src\trampoline.c vendor\src\hde\hde64.c /Fo:build\
if errorlevel 1 exit /b 1
cl /nologo /LD /O2 /W4 /WX /EHsc /MT /std:c++17 SpellMods.cpp build\buffer.obj build\hook.obj build\trampoline.obj build\hde64.obj /Fo:build\SpellMods.obj /link /OUT:build\SpellWorkspace.SpellMods.dll /IMPLIB:build\SpellMods.lib bcrypt.lib user32.lib
if errorlevel 1 exit /b 1
cl /nologo /O2 /W4 /WX /EHsc /MT /std:c++17 ClientLaunch.cpp /Fo:build\ClientLaunch.obj /link /SUBSYSTEM:WINDOWS /OUT:build\SpellWorkspace.Client.exe user32.lib
if errorlevel 1 exit /b 1
cl /nologo /O2 /W4 /WX /EHsc /MT /std:c++17 OverlayTests.cpp /Fo:build\OverlayTests.obj /Fe:build\OverlayTests.exe
exit /b %errorlevel%
