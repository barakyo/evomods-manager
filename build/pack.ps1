<#
.SYNOPSIS
  Publish EvoMods.App and pack it into a Velopack release.

.DESCRIPTION
  WinUI 3 cannot produce a single-file exe — the native Windows App SDK runtime files have to sit
  loose next to it — so the installer is the delivery mechanism, not a convenience. This script is
  the whole of it: publish self-contained, then hand the folder to vpk.

  Requires the Velopack CLI:  dotnet tool install -g vpk

.EXAMPLE
  .\build\pack.ps1 -Version 0.1.0
  .\build\pack.ps1 -Version 0.1.0 -Feed C:\some\folder   # also point a local feed at the output
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Configuration = 'Release',
    [string] $PublishDir    = '.\publish',
    [string] $ReleaseDir    = '.\releases'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo

try {
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        throw "vpk not found. Install it with: dotnet tool install -g vpk"
    }

    # A stale publish folder is worse than none: dotnet publish does not remove files that a
    # previous run left behind, so a deleted asset can survive into the package.
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

    dotnet publish EvoMods.App/EvoMods.App.csproj `
        -c $Configuration -o $PublishDir -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    # --packId is permanent: it is the identity Windows and the updater use to recognise an
    # existing install, so changing it later orphans everyone's installation.
    vpk pack `
        --packId      EvoMods.Manager `
        --packVersion $Version `
        --packDir     $PublishDir `
        --mainExe     EvoMods.exe `
        --packTitle   "EvoMods Manager" `
        -o            $ReleaseDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

    Write-Host ""
    Get-ChildItem $ReleaseDir -File |
        Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
        Format-Table -AutoSize
}
finally {
    Pop-Location
}
