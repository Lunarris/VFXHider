@echo off
setlocal

set PROJECT=%~dp0VFXHider.csproj
set OUTPUT=%~dp0bin\Release
set TARGET=Z:\PLugins Dev\VFXHider

echo Building Release...
dotnet build "%PROJECT%" -c Release
if errorlevel 1 goto :builderror

if not exist "%OUTPUT%\VFXHider.dll" (
    echo.
    echo Build output not found at %OUTPUT%.
    exit /b 1
)

if not exist "Z:\" (
    echo.
    echo Drive Z: is not available.
    exit /b 1
)

echo.
echo Copying to %TARGET%...
robocopy "%OUTPUT%" "%TARGET%" /MIR /XD "%OUTPUT%\VFXHider" /XF *.pdb /R:3 /W:1 /NFL /NDL /NJH /NP
if errorlevel 8 goto :copyerror

echo.
echo VFX Hider deployed to %TARGET%
echo Dev Plugin Location: %TARGET%\VFXHider.dll
exit /b 0

:builderror
echo.
echo Build failed.
exit /b 1

:copyerror
echo.
echo Copy failed. If the game is running, unload VFX Hider in /xlplugins and run this again.
exit /b 1
