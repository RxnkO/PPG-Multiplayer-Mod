@echo off
REM ============================================================
REM  Builds PPGMultiplayer.dll from src\Mod.cs
REM  Run this on Windows. No Visual Studio needed.
REM  Re-run it every time you change RELAY_URL or the code.
REM ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

REM --- locate the game's Managed folder (two levels up from Mods\PPG_Multiplayer) ---
set "MANAGED=%~dp0..\..\People Playground_Data\Managed"
if not exist "!MANAGED!\Assembly-CSharp.dll" (
  echo [ERROR] Could not find the game's Managed folder. I looked here:
  echo     !MANAGED!
  echo Edit the MANAGED line in this .bat to point at your
  echo "People Playground_Data\Managed" folder, then re-run.
  pause & exit /b 1
)
echo Using game assemblies: !MANAGED!

REM --- find the C# compiler that ships with .NET Framework ---
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "!CSC!" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "!CSC!" (
  echo [ERROR] csc.exe not found. Install ".NET Framework 4.x"
  echo or build the project in Visual Studio instead.
  pause & exit /b 1
)
echo Using compiler: !CSC!

REM --- build a response file (handles the long list of references) ---
set "RSP=%~dp0_build.rsp"
> "!RSP!" echo /nologo
>> "!RSP!" echo /target:library
>> "!RSP!" echo /out:"%~dp0PPGMultiplayer.dll"
for %%f in ("!MANAGED!\*.dll") do >> "!RSP!" echo /reference:"%%f"
>> "!RSP!" echo "%~dp0src\Mod.cs"

echo Compiling...
REM /noconfig and /nostdlib+ MUST be on the command line (they're ignored inside a response file).
REM This stops csc from auto-importing Windows' System.*/mscorlib, which would clash with the
REM game's own copies in Managed (the CS1703 "already imported" errors).
"!CSC!" /noconfig /nostdlib+ @"!RSP!"
set ERR=%ERRORLEVEL%
del "!RSP!" >nul 2>&1

echo.
if exist "%~dp0PPGMultiplayer.dll" if "%ERR%"=="0" (
  echo [OK] Built PPGMultiplayer.dll
  echo Now launch People Playground and Recompile / reload the mod.
  pause & exit /b 0
)
echo [FAILED] Compilation errors above. Copy them and send them to Claude.
pause & exit /b 1
