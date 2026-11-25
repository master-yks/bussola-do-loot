@echo off
setlocal
cd /d "%~dp0"
if exist "..\.ferramentas\dotnet\dotnet.exe" (
    set "PATH=%~dp0..\.ferramentas\dotnet;%PATH%"
    set "DOTNET_ROOT=%~dp0..\.ferramentas\dotnet"
)
if exist "..\.ferramentas\packages" set "NUGET_PACKAGES=%~dp0..\.ferramentas\packages"
dotnet run --project src\ChestCounter.Gui -c Testes
if errorlevel 1 pause
