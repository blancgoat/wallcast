$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$toolsDir = Join-Path $root '.tools'
$archive = Join-Path $toolsDir 'ffmpeg-9.0.1.zip'
$unpack = Join-Path $toolsDir 'ffmpeg-9.0.1'
$target = Join-Path $toolsDir 'capture'
New-Item -ItemType Directory -Force $toolsDir,$target | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.1/ffmpeg-9.0.1-essentials_build.zip' -OutFile $archive
}
$expected = 'fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'FFmpeg archive checksum mismatch.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $unpack -Force
$build = Join-Path $unpack 'ffmpeg-9.0.1-essentials_build'
Copy-Item -LiteralPath (Join-Path $build 'bin/ffmpeg.exe') -Destination $target
Copy-Item -LiteralPath (Join-Path $build 'LICENSE') -Destination (Join-Path $target 'LICENSE-FFmpeg.txt')
Copy-Item -LiteralPath (Join-Path $build 'README.txt') -Destination (Join-Path $target 'README-FFmpeg.txt')
Write-Output "Capture engine ready: $target"
