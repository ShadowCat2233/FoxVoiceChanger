[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$cargoPath = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
[xml]$buildProperties = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
$productVersion = [string]$buildProperties.Project.PropertyGroup.Version
$cargoManifest = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'native\Cargo.toml')
$cargoVersion = [regex]::Match($cargoManifest, '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
if ([string]::IsNullOrWhiteSpace($productVersion) -or $cargoVersion -ne $productVersion) {
    throw "Desktop/setup version '$productVersion' does not match native version '$cargoVersion'."
}

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
& $cargoPath '+stable-x86_64-pc-windows-msvc' build `
    --manifest-path (Join-Path $projectRoot 'native\Cargo.toml') `
    --package foxvoice-converter --release
if ($LASTEXITCODE -ne 0) { throw 'RVC model converter release build failed.' }

$artifactsDirectory = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactsDirectory | Out-Null
$dependencyCache = Join-Path $artifactsDirectory '.dependency-cache'
New-Item -ItemType Directory -Force -Path $dependencyCache | Out-Null
$windowsAppSdkVersion = '2.3.9'
$windowsAppSdkHash = '230BC605A3FC9ED689B2117056C5274923BF58B453FA44EDDE18A168BBF628BE'
$windowsAppSdkPackage = Join-Path $dependencyCache "Microsoft.WindowsAppSDK.Foundation.$windowsAppSdkVersion.nupkg"
$windowsAppSdkExtract = Join-Path $dependencyCache "Microsoft.WindowsAppSDK.Foundation.$windowsAppSdkVersion"
if (-not (Test-Path -LiteralPath $windowsAppSdkPackage)) {
    $partialPackage = "$windowsAppSdkPackage.partial"
    Invoke-WebRequest -UseBasicParsing `
        -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.foundation/$windowsAppSdkVersion/microsoft.windowsappsdk.foundation.$windowsAppSdkVersion.nupkg" `
        -OutFile $partialPackage
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $partialPackage).Hash -ne $windowsAppSdkHash) {
        throw 'Windows App SDK Foundation package SHA-256 mismatch.'
    }
    Move-Item -LiteralPath $partialPackage -Destination $windowsAppSdkPackage
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $windowsAppSdkPackage).Hash -ne $windowsAppSdkHash) {
    throw 'Cached Windows App SDK Foundation package SHA-256 mismatch.'
}
if (-not (Test-Path -LiteralPath (Join-Path $windowsAppSdkExtract 'runtimes\win-x64\native\Microsoft.WindowsAppRuntime.Bootstrap.dll'))) {
    Expand-Archive -LiteralPath $windowsAppSdkPackage -DestinationPath $windowsAppSdkExtract -Force
}
$windowsMlBootstrap = Join-Path $windowsAppSdkExtract 'runtimes\win-x64\native\Microsoft.WindowsAppRuntime.Bootstrap.dll'
$windowsAppSdkLicense = Join-Path $windowsAppSdkExtract 'license.txt'
if (-not (Test-Path -LiteralPath $windowsMlBootstrap) -or -not (Test-Path -LiteralPath $windowsAppSdkLicense)) {
    throw 'Windows App SDK bootstrapper or redistributable license is missing.'
}
Copy-Item -LiteralPath $windowsMlBootstrap -Destination (Join-Path $projectRoot 'native\target\release\Microsoft.WindowsAppRuntime.Bootstrap.dll') -Force
$outputDirectory = Join-Path $artifactsDirectory 'FoxVoice-win-x64'
$stagingDirectory = Join-Path $artifactsDirectory ".FoxVoice-win-x64-staging-$PID"
$archivePath = Join-Path $artifactsDirectory 'FoxVoice-win-x64.zip'
$archiveStagingPath = Join-Path $artifactsDirectory ".FoxVoice-win-x64-$PID.zip"
$setupPath = Join-Path $artifactsDirectory 'FoxVoiceSetup.exe'
$setupStagingDirectory = Join-Path $artifactsDirectory ".FoxVoice-setup-staging-$PID"
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
Assert-ArtifactChild $setupPath
Assert-ArtifactChild $setupStagingDirectory
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
Copy-Item -LiteralPath (Join-Path $projectRoot 'native\target\release\foxvoice-converter.exe') `
    -Destination (Join-Path $stagingDirectory 'foxvoice-converter.exe') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') `
    -Destination (Join-Path $stagingDirectory 'THIRD_PARTY_NOTICES.md') -Force
Copy-Item -LiteralPath $windowsMlBootstrap `
    -Destination (Join-Path $stagingDirectory 'Microsoft.WindowsAppRuntime.Bootstrap.dll') -Force
$licenseDirectory = Join-Path $stagingDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
Copy-Item -LiteralPath $windowsAppSdkLicense `
    -Destination (Join-Path $licenseDirectory 'WindowsAppSDK-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'native\vendor\vc-app\LICENSE') `
    -Destination (Join-Path $licenseDirectory 'vc-rs-MIT.txt') -Force
$debugSymbols = Join-Path $stagingDirectory 'FoxVoice.pdb'
if (Test-Path -LiteralPath $debugSymbols) { Remove-Item -LiteralPath $debugSymbols -Force }

$hashes = Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse | Where-Object Name -ne 'SHA256SUMS.json' | ForEach-Object {
    [pscustomobject]@{ File = [IO.Path]::GetRelativePath($stagingDirectory, $_.FullName); SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash }
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

New-Item -ItemType Directory -Path $setupStagingDirectory | Out-Null
& dotnet publish (Join-Path $projectRoot 'setup\FoxVoice.Setup\FoxVoice.Setup.csproj') `
    --configuration $Configuration --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PayloadPath="$archivePath" --output $setupStagingDirectory
if ($LASTEXITCODE -ne 0) { throw 'FoxVoice installer build failed.' }
$builtSetup = Join-Path $setupStagingDirectory 'FoxVoiceSetup.exe'
if (-not (Test-Path -LiteralPath $builtSetup)) { throw 'FoxVoice installer output is missing.' }
$setupBackupPath = $null
try {
    if (Test-Path -LiteralPath $setupPath) {
        $setupBackupPath = Join-Path $artifactsDirectory ".FoxVoiceSetup-backup-$PID.exe"
        Assert-ArtifactChild $setupBackupPath
        Move-Item -LiteralPath $setupPath -Destination $setupBackupPath
    }
    Move-Item -LiteralPath $builtSetup -Destination $setupPath
}
catch {
    if ($setupBackupPath -and (Test-Path -LiteralPath $setupBackupPath) -and -not (Test-Path -LiteralPath $setupPath)) {
        Move-Item -LiteralPath $setupBackupPath -Destination $setupPath
    }
    throw
}
if ($setupBackupPath -and (Test-Path -LiteralPath $setupBackupPath)) { Remove-Item -LiteralPath $setupBackupPath -Force }
Remove-Item -LiteralPath $setupStagingDirectory -Recurse -Force
$setupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setupPath).Hash
Set-Content -Encoding ascii -LiteralPath "$setupPath.sha256" -Value "$setupHash  FoxVoiceSetup.exe"
Write-Host "FoxVoice release is ready: $outputDirectory"
Write-Host "Portable archive: $archivePath"
Write-Host "Per-user installer: $setupPath"
