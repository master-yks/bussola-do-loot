#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$artifacts = Join-Path $root 'artifacts'
$output = Join-Path $artifacts "BussolaDoLoot-win-x64-$stamp"
$build = Join-Path $artifacts "build-$stamp"

Push-Location -LiteralPath $root
try {
    & dotnet run --project tests/ChestCounter.Checks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'As verificações de contagem falharam.' }

    & dotnet run --project tests/ChestCounter.MapChecks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'As verificações de rotas falharam.' }

    [void](New-Item -ItemType Directory -Path $output)
    foreach ($app in 'Gui', 'Live') {
        & dotnet publish "src/ChestCounter.$app" --artifacts-path $build -c Release -r win-x64 --self-contained true -o $output -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar ChestCounter.$app." }
    }
    $licenses = New-Item -ItemType Directory -Path (Join-Path $output 'licencas')
    Copy-Item -LiteralPath 'LICENSE', 'lib/LICENSE-LED-N-ChestCounter', 'lib/LICENSE-FortniteReplayDecompressor' -Destination $licenses.FullName
    Copy-Item -LiteralPath 'lib/OozSharp/COPYING' -Destination (Join-Path $licenses.FullName 'GPL-3.0-OozSharp.txt')
    # Keep the runtime notices for the versions selected by this SDK.
    $assets = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $build 'obj/ChestCounter.Gui/project.assets.json') | ConvertFrom-Json
    foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
        foreach ($pack in $framework.downloadDependencies) {
            $version = $pack.version.Trim('[', ']').Split(',')[0].Trim()
            foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
                $location = Join-Path $cache "$($pack.name)/$version"
                if (!(Test-Path -LiteralPath $location)) { continue }
                foreach ($notice in Get-ChildItem -LiteralPath $location -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.txt)?$') {
                    Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses.FullName "$($pack.name)-$($notice.Name)")
                }
                break
            }
        }
    }
    Copy-Item -LiteralPath 'docs/LEIA-ME.txt', 'docs/MANUAL.md', 'scripts/Criar atalhos.cmd' -Destination $output
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($output, "$output.zip")
    Write-Host "Distribuição pronta em $output.zip"
}
finally { Pop-Location }
