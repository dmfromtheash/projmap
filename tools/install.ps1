# Publishes projmap.exe + projmap-cli.exe and puts them on PATH (via ~/.local/bin shims).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/install.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $env:LOCALAPPDATA 'Programs\projmap'
$bin = Join-Path $env:USERPROFILE '.local\bin'

# a running viewer locks the exe
Get-Process projmap -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | ForEach-Object {
    throw "projmap.exe запущен ($($_.Id)) — закройте окно и повторите."
}

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-o', $dest, '--nologo', '-v', 'q')
dotnet publish (Join-Path $repo 'src\ProjMap.App\ProjMap.App.csproj') @common
if ($LASTEXITCODE) { throw 'publish ProjMap.App failed' }
dotnet publish (Join-Path $repo 'src\ProjMap.Cli\ProjMap.Cli.csproj') @common
if ($LASTEXITCODE) { throw 'publish ProjMap.Cli failed' }

New-Item -ItemType Directory -Force $bin | Out-Null
# `projmap [folder]` opens the window for the current (or given) project without blocking the terminal.
Set-Content -Encoding ascii (Join-Path $bin 'projmap.cmd') "@echo off`r`nstart `"`" `"$dest\projmap.exe`" %*"
Set-Content -Encoding ascii (Join-Path $bin 'projmap-cli.cmd') "@echo off`r`n`"$dest\projmap-cli.exe`" %*"
Write-Host "Установлено: $dest"
Write-Host "Команды: projmap [папка]   |   projmap-cli scan <папка>"
