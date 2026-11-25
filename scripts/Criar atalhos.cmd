@echo off
setlocal
set "BUSSOLA_APP_DIR=%~dp0"
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $exe=Get-Item -LiteralPath (Join-Path $env:BUSSOLA_APP_DIR 'BussolaDoLoot.exe'); $name=$exe.VersionInfo.ProductName; $shell=New-Object -ComObject WScript.Shell; $link=$shell.CreateShortcut((Join-Path $shell.SpecialFolders.Item('Desktop') ($name+'.lnk'))); $link.TargetPath=$exe.FullName; $link.WorkingDirectory=$exe.DirectoryName; $link.IconLocation=$exe.FullName+',0'; $link.Description=$name; $link.Save()"
if errorlevel 1 pause
