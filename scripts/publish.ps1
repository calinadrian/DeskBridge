param([string]$OutputDirectory = 'artifacts')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$appOutput = Join-Path $outputRoot 'app'
$package = Join-Path $outputRoot 'package'
New-Item -ItemType Directory -Force $appOutput,$package | Out-Null
dotnet publish (Join-Path $repoRoot 'Source\DeskBridge.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $appOutput
if ($LASTEXITCODE -ne 0) { throw 'Publishing DeskBridge failed.' }
$exe = Join-Path $appOutput 'DeskBridge.exe'
$asset = Join-Path $outputRoot 'DeskBridge-win-x64.exe'
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'Source\DeskBridge.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$manifest = @{version=$version; tag="v$version"} | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $outputRoot 'DeskBridge-update.json'),$manifest,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $exe -Destination $asset -Force
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($asset + '.sha256', "$hash  DeskBridge-win-x64.exe`n", [Text.Encoding]::ASCII)
Copy-Item -LiteralPath $exe -Destination (Join-Path $package 'DeskBridge.exe') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'),(Join-Path $repoRoot 'Allow-Home-Network.ps1') -Destination $package -Force
Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $outputRoot 'DeskBridge-Windows-x64.zip') -Force
Write-Host "Release assets: $outputRoot"
