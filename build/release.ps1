<#
.SYNOPSIS
  Pack a version and upload it to GitHub Releases.

.DESCRIPTION
  Deliberately separate from build\pack.ps1. Packing is safe and repeatable — run it as often as you
  like. Publishing is neither: a release is visible the moment it lands, and every installed copy of
  the app is watching that feed. Keeping them apart means nobody reaches a live release by adding a
  flag to a command they were already running.

  Uploads as a DRAFT unless -Publish is given, so a release can be looked at on GitHub before anyone
  is offered it.

  Needs build\secrets.ps1 (gitignored) holding the token. See build\secrets.example.ps1.

.EXAMPLE
  .\build\release.ps1 -Version 0.3.0                # pack and upload as a draft
  .\build\release.ps1 -Version 0.3.0 -Publish       # ... and publish it
  .\build\release.ps1 -Version 0.4.0-beta.1 -Pre    # a prerelease, for the beta channel
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [switch] $Publish,
    [switch] $Pre
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$RepoUrl = 'https://github.com/barakyo/evomods-manager'

Push-Location $repo
try {
    $secrets = Join-Path $PSScriptRoot 'secrets.ps1'
    if (-not (Test-Path $secrets)) {
        throw "No build\secrets.ps1. Copy build\secrets.example.ps1 to it and add your token."
    }

    . $secrets
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        throw "GITHUB_TOKEN is empty in build\secrets.ps1."
    }

    # Everything the packaging step already knows how to do, unchanged.
    & (Join-Path $PSScriptRoot 'pack.ps1') -Version $Version

    $args = @(
        'upload', 'github',
        '--repoUrl', $RepoUrl,
        '--token', $env:GITHUB_TOKEN,
        '-o', '.\releases'
    )
    if ($Publish) { $args += @('--publish', 'true') }
    if ($Pre) { $args += @('--pre', 'true') }

    Write-Host ""
    Write-Host "Uploading $Version to $RepoUrl as $(if ($Publish) { 'a PUBLISHED release' } else { 'a DRAFT' })..."

    # The token is in $args rather than in the transcript of this script, and vpk does not echo it.
    & vpk @args
    if ($LASTEXITCODE -ne 0) { throw "vpk upload failed" }

    Write-Host ""
    if ($Publish) {
        Write-Host "Live. Installed copies will see $Version on their next check."
    } else {
        Write-Host "Uploaded as a draft. Publish it at $RepoUrl/releases when it looks right."
    }
}
finally {
    # So a token does not outlive the command that needed it in a long-running shell.
    $env:GITHUB_TOKEN = $null
    Pop-Location
}
