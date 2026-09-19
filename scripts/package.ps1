#requires -Version 5.1
<#
  Builds the zip that goes on a release: a clean publish of Wallcast, archived with the licences it is
  obliged to carry.

  Publishing into a folder that already has a build can leave a stale framework assembly behind when its
  timestamp is newer than the package version's, so this always starts from an empty folder.

  Archiving uses tar.exe, which ships with Windows 10 and 11. PowerShell's own Compress-Archive writes
  entry names with backslashes, which Windows reads but unzip on macOS and Linux turns into one long
  filename per file.
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Dotnet = (Join-Path $PSScriptRoot '..\.tools\dotnet\dotnet.exe')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$stage = Join-Path $root "artifacts\Wallcast"
if (-not (Test-Path $Dotnet)) { $Dotnet = 'dotnet' }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
& $Dotnet publish (Join-Path $root 'Wallcast') -c Release -r $Runtime --self-contained true -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# A missing licence or a missing capture engine turns a release into a broken download, so both are
# checked before anything is shipped rather than after someone reports it.
foreach ($needed in 'Wallcast.exe', 'LICENSE', 'THIRD-PARTY.txt', 'capture\ffmpeg.exe',
                    'capture\LICENSE-FFmpeg.txt', 'libvlc\win-x64\libvlc.dll') {
    if (-not (Test-Path (Join-Path $stage $needed))) { throw "the publish is missing $needed" }
}
foreach ($unwanted in 'libvlc\win-x86', 'libvlc\win-arm64') {
    if (Test-Path (Join-Path $stage $unwanted)) { throw "$unwanted should have been trimmed" }
}

# The version comes from what was actually built, so the file name can never disagree with the binary
# inside it. Name, then version, then platform - the order Node and PowerShell use for their downloads.
$built = Get-Item (Join-Path $stage 'Wallcast.exe')
$version = $built.VersionInfo.ProductVersion
if (-not $version) { throw "the build carries no version" }
$version = ($version -split '\+')[0]
$zip = Join-Path $root "artifacts\Wallcast-v$version-$Runtime.zip"

if (Test-Path $zip) { Remove-Item $zip -Force }
Push-Location (Split-Path $stage -Parent)
try {
    & tar.exe -a -c -f $zip (Split-Path $stage -Leaf)
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
}
finally { Pop-Location }

$folderMb = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Output "staged $stage ($folderMb MB)"
Write-Output "wrote  $zip ($zipMb MB)"
Write-Output "upload that one file; it unpacks to a single Wallcast folder."
Write-Output "tag it with: git tag -a v$version -m ""Wallcast v$version"""
