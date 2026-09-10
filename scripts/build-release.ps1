[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$cargoPath = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

if (-not (Test-Path -LiteralPath $cargoPath)) {
    throw 'Rust is not installed. Run scripts\bootstrap-native.ps1 first.'
}
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Build Tools are not installed. Run scripts\bootstrap-native.ps1 first.'
}

$installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($installation)) { throw 'Unable to locate MSVC Build Tools.' }
$developerCommand = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
& cmd.exe /d /s /c "`"$developerCommand`" -arch=x64 -host_arch=x64 >nul && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "Env:$($matches[1])" -Value $matches[2] }
}

& $cargoPath '+stable-x86_64-pc-windows-msvc' build `
    --manifest-path (Join-Path $projectRoot 'native\Cargo.toml') `
    --package foxvoice-supervisor --features wasapi --release
if ($LASTEXITCODE -ne 0) { throw 'Native release build failed.' }
& $cargoPath '+stable-x86_64-pc-windows-msvc' build `
    --manifest-path (Join-Path $projectRoot 'native\Cargo.toml') `
    --package foxvoice-engine --features windowsml --release
if ($LASTEXITCODE -ne 0) { throw 'RVC engine release build failed.' }

$outputDirectory = Join-Path $projectRoot 'artifacts\FoxVoice-win-x64'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
& dotnet publish (Join-Path $projectRoot 'desktop\FoxVoice.Desktop\FoxVoice.Desktop.csproj') `
    --configuration $Configuration --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Desktop release build failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'native\target\release\foxvoice-supervisor.exe') `
    -Destination (Join-Path $outputDirectory 'foxvoice-supervisor.exe') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'native\target\release\foxvoice-engine.exe') `
    -Destination (Join-Path $outputDirectory 'foxvoice-engine.exe') -Force
$debugSymbols = Join-Path $outputDirectory 'FoxVoice.pdb'
if (Test-Path -LiteralPath $debugSymbols) { Remove-Item -LiteralPath $debugSymbols -Force }

$hashes = Get-ChildItem -LiteralPath $outputDirectory -File | Where-Object Name -ne 'SHA256SUMS.json' | ForEach-Object {
    [pscustomobject]@{ File = $_.Name; SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash }
}
$hashes | ConvertTo-Json | Set-Content -Encoding utf8 -LiteralPath (Join-Path $outputDirectory 'SHA256SUMS.json')
$archivePath = Join-Path $projectRoot 'artifacts\FoxVoice-win-x64.zip'
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash
Set-Content -Encoding ascii -LiteralPath "$archivePath.sha256" -Value "$archiveHash  FoxVoice-win-x64.zip"
Write-Host "FoxVoice release is ready: $outputDirectory"
Write-Host "Portable archive: $archivePath"
