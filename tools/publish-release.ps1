# Builds the release zip: projmap.exe (the window) and projmap-cli.exe (command line), self-contained for Windows x64,
# so nothing else has to be installed (WebView2 comes with Windows 10/11).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish-release.ps1 -Out <folder>
param([Parameter(Mandatory)] [string] $Out)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $repo 'src\ProjMap.App\ProjMap.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = Join-Path $Out "projmap-$version-win-x64"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none',
            '-o', $stage, '--nologo', '-v', 'q')
dotnet publish (Join-Path $repo 'src\ProjMap.App\ProjMap.App.csproj') @common
if ($LASTEXITCODE) { throw 'publish ProjMap.App failed' }
dotnet publish (Join-Path $repo 'src\ProjMap.Cli\ProjMap.Cli.csproj') @common
if ($LASTEXITCODE) { throw 'publish ProjMap.Cli failed' }
Copy-Item (Join-Path $repo 'README.md') $stage
$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host $zip
