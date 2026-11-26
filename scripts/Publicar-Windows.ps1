#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Resolve-DotNet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $sdks = & $cmd.Source --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks -match '^10\.')) {
            return $cmd.Source
        }
    }
    $local = Join-Path (Split-Path $root -Parent) '.ferramentas\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) {
        $env:DOTNET_ROOT = Split-Path $local -Parent
        return $local
    }
    throw 'SDK .NET 10 não encontrado no PATH.'
}

function Resolve-Iscc {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

$dotnet = Resolve-DotNet
$csproj = [xml](Get-Content -LiteralPath (Join-Path $root 'src\ChestCounter.Gui\ChestCounter.Gui.csproj') -Encoding UTF8)
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($version)) { $version = '1.1.0' }

$artifacts = Join-Path $root 'artifacts'
$releaseDir = if ($OutputDir) { $OutputDir } else { Join-Path $artifacts 'release' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$staging = Join-Path $artifacts "_staging-$stamp"
$portableDir = Join-Path $staging 'BussolaDoLoot'
$buildDir = Join-Path $staging 'build'

Push-Location -LiteralPath $root
try {
    if (-not $SkipTests) {
        Write-Host '==> Executando verificações de contagem e rotas...'
        & $dotnet run --project tests/ChestCounter.Checks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'As verificações de contagem falharam.' }

        & $dotnet run --project tests/ChestCounter.MapChecks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'As verificações de rotas falharam.' }
    }

    [void](New-Item -ItemType Directory -Path $portableDir -Force)
    [void](New-Item -ItemType Directory -Path $releaseDir -Force)

    Write-Host '==> Publicando executáveis únicos autossuficientes (win-x64)...'
    foreach ($app in 'Gui', 'Live') {
        & $dotnet publish "src/ChestCounter.$app" `
            --artifacts-path $buildDir `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -o $portableDir `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:IncludeAllContentForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:SatelliteResourceLanguages=pt-BR `
            -p:DebugType=None `
            -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar ChestCounter.$app." }
    }

    $licenses = New-Item -ItemType Directory -Path (Join-Path $portableDir 'licencas') -Force
    Copy-Item -LiteralPath 'LICENSE', 'lib/LICENSE-LED-N-ChestCounter', 'lib/LICENSE-FortniteReplayDecompressor' -Destination $licenses.FullName
    Copy-Item -LiteralPath 'lib/OozSharp/COPYING' -Destination (Join-Path $licenses.FullName 'GPL-3.0-OozSharp.txt')

    $assets = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $buildDir 'obj/ChestCounter.Gui/project.assets.json') | ConvertFrom-Json
    foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
        foreach ($pack in $framework.downloadDependencies) {
            $packVersion = $pack.version.Trim('[', ']').Split(',')[0].Trim()
            foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
                $location = Join-Path $cache "$($pack.name)/$packVersion"
                if (!(Test-Path -LiteralPath $location)) { continue }
                foreach ($notice in Get-ChildItem -LiteralPath $location -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.txt)?$') {
                    Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses.FullName "$($pack.name)-$($notice.Name)")
                }
                break
            }
        }
    }

    Copy-Item -LiteralPath 'docs/LEIA-ME.txt', 'docs/MANUAL.md', 'scripts/Criar atalhos.cmd' -Destination $portableDir

    $portableZip = Join-Path $releaseDir 'BussolaDoLoot-Portable-win-x64.zip'
    if (Test-Path -LiteralPath $portableZip) { Remove-Item -LiteralPath $portableZip -Force }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($portableDir, $portableZip, [IO.Compression.CompressionLevel]::Optimal, $false)

    $publishedFiles = [System.Collections.Generic.List[string]]::new()
    $publishedFiles.Add($portableZip)

    if (-not $SkipInstaller) {
        $iscc = Resolve-Iscc
        if (-not $iscc) {
            throw 'Inno Setup 6 (ISCC.exe) não encontrado. Instale o Inno Setup 6 ou use -SkipInstaller.'
        }
        Write-Host '==> Gerando instalador Windows (Inno Setup 6)...'
        $iss = Join-Path $root 'installer\BussolaDoLoot.iss'
        & $iscc `
            "/DMyAppVersion=$version" `
            "/DSourceDir=$portableDir" `
            "/DOutputDir=$releaseDir" `
            "/DOutputBaseFilename=BussolaDoLoot-Setup-win-x64" `
            $iss
        if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador Windows.' }
        $publishedFiles.Add((Join-Path $releaseDir 'BussolaDoLoot-Setup-win-x64.exe'))
    }

    $sumsPath = Join-Path $releaseDir 'SHA256SUMS.txt'
    $sums = foreach ($file in $publishedFiles) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path $file -Leaf)"
    }
    Set-Content -LiteralPath $sumsPath -Value $sums -Encoding UTF8

    Write-Host ''
    Write-Host "Distribuição v$version pronta em ${releaseDir}:"
    Get-ChildItem -LiteralPath $releaseDir | ForEach-Object {
        Write-Host ("  - {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
    }
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
