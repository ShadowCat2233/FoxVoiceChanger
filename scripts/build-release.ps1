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

$artifactsDirectory = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactsDirectory | Out-Null
$outputDirectory = Join-Path $artifactsDirectory 'FoxVoice-win-x64'
$stagingDirectory = Join-Path $artifactsDirectory ".FoxVoice-win-x64-staging-$PID"
$archivePath = Join-Path $artifactsDirectory 'FoxVoice-win-x64.zip'
$archiveStagingPath = Join-Path $artifactsDirectory ".FoxVoice-win-x64-$PID.zip"
$artifactsRoot = [IO.Path]::GetFullPath($artifactsDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Assert-ArtifactChild([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the artifacts directory: $fullPath"
    }
}

Assert-ArtifactChild $outputDirectory
Assert-ArtifactChild $stagingDirectory
Assert-ArtifactChild $archivePath
Assert-ArtifactChild $archiveStagingPath
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
& dotnet publish (Join-Path $projectRoot 'desktop\FoxVoice.Desktop\FoxVoice.Desktop.csproj') `
    --configuration $Configuration --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $stagingDirectory
if ($LASTEXITCODE -ne 0) { throw 'Desktop release build failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'native\target\release\foxvoice-supervisor.exe') `
    -Destination (Join-Path $stagingDirectory 'foxvoice-supervisor.exe') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'native\target\release\foxvoice-engine.exe') `
    -Destination (Join-Path $stagingDirectory 'foxvoice-engine.exe') -Force
$debugSymbols = Join-Path $stagingDirectory 'FoxVoice.pdb'
if (Test-Path -LiteralPath $debugSymbols) { Remove-Item -LiteralPath $debugSymbols -Force }

$hashes = Get-ChildItem -LiteralPath $stagingDirectory -File | Where-Object Name -ne 'SHA256SUMS.json' | ForEach-Object {
    [pscustomobject]@{ File = $_.Name; SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash }
}
$hashes | ConvertTo-Json | Set-Content -Encoding utf8 -LiteralPath (Join-Path $stagingDirectory 'SHA256SUMS.json')
Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $archiveStagingPath -CompressionLevel Optimal

$backupDirectory = $null
try {
    if (Test-Path -LiteralPath $outputDirectory) {
        $backupDirectory = Join-Path $artifactsDirectory ".FoxVoice-win-x64-backup-$PID"
        Assert-ArtifactChild $backupDirectory
        Move-Item -LiteralPath $outputDirectory -Destination $backupDirectory
    }
    Move-Item -LiteralPath $stagingDirectory -Destination $outputDirectory
}
catch {
    if ($backupDirectory -and (Test-Path -LiteralPath $backupDirectory) -and -not (Test-Path -LiteralPath $outputDirectory)) {
        Move-Item -LiteralPath $backupDirectory -Destination $outputDirectory
    }
    throw
}
if ($backupDirectory -and (Test-Path -LiteralPath $backupDirectory)) {
    Remove-Item -LiteralPath $backupDirectory -Recurse -Force
}

$archiveBackupPath = $null
try {
    if (Test-Path -LiteralPath $archivePath) {
        $archiveBackupPath = Join-Path $artifactsDirectory ".FoxVoice-win-x64-backup-$PID.zip"
        Assert-ArtifactChild $archiveBackupPath
        Move-Item -LiteralPath $archivePath -Destination $archiveBackupPath
    }
    Move-Item -LiteralPath $archiveStagingPath -Destination $archivePath
}
catch {
    if ($archiveBackupPath -and (Test-Path -LiteralPath $archiveBackupPath) -and -not (Test-Path -LiteralPath $archivePath)) {
        Move-Item -LiteralPath $archiveBackupPath -Destination $archivePath
    }
    throw
}
if ($archiveBackupPath -and (Test-Path -LiteralPath $archiveBackupPath)) {
    Remove-Item -LiteralPath $archiveBackupPath -Force
}

$archiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash
Set-Content -Encoding ascii -LiteralPath "$archivePath.sha256" -Value "$archiveHash  FoxVoice-win-x64.zip"
Write-Host "FoxVoice release is ready: $outputDirectory"
Write-Host "Portable archive: $archivePath"
