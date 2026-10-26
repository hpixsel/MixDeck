<#
.SYNOPSIS
  Builds a MixDeck release: runs the tests, publishes the app and packs an installer (Setup.exe)
  plus update packages with Velopack (https://velopack.io).

.EXAMPLE
  .\build\release.ps1                     # version from MixDeck.csproj
  .\build\release.ps1 -Version 0.3.0      # explicit version
  .\build\release.ps1 -Upload -RepoUrl https://github.com/<login>/MixDeck -Token <github-token>

  Output: releases\MixDeck-win-Setup.exe (installer) and the files the in-app updater downloads.
  With -Upload the release is published to GitHub Releases, where installed copies find it.
#>
param(
    [string]$Version,
    [switch]$Upload,
    [string]$RepoUrl,
    [string]$Token
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$project = "$root\src\MixDeck\MixDeck.csproj"
if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
Write-Host "MixDeck $Version" -ForegroundColor Cyan

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { dotnet tool install -g vpk }

dotnet test "$root\tests\MixDeck.Tests" -c Release --nologo
if ($LASTEXITCODE) { throw "Tests failed" }

$publish = "$root\build\publish"
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish $project -c Release -r win-x64 --self-contained false -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE) { throw "Publish failed" }

# Setup.exe installs the .NET 10 desktop + ASP.NET Core runtimes if they are missing
vpk pack --packId MixDeck --packVersion $Version --packDir $publish --mainExe MixDeck.exe `
    --packTitle MixDeck --packAuthors "Kacper Adamus" --icon "$root\assets\MixDeck.ico" `
    --framework net10-x64-desktop,net10-x64-aspnetcore --outputDir "$root\releases"
if ($LASTEXITCODE) { throw "vpk pack failed" }

if ($Upload) {
    if (-not $RepoUrl -or -not $Token) { throw "-Upload needs -RepoUrl and -Token" }
    vpk upload github --repoUrl $RepoUrl --token $Token --outputDir "$root\releases" --publish --releaseName "MixDeck $Version" --tag "v$Version"
}

Write-Host "Done: $root\releases" -ForegroundColor Green
