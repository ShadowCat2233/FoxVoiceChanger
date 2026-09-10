[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipBuildToolsInstall,
    [ValidateSet('msvc', 'gnu')]
    [string]$Toolchain = 'msvc'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$cargoPath = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
$rustupPath = Join-Path $env:USERPROFILE '.cargo\bin\rustup.exe'
$bootstrapDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'FoxVoiceBootstrap'
$rustupInstaller = Join-Path $bootstrapDirectory 'rustup-init.exe'
$rustupChecksum = Join-Path $bootstrapDirectory 'rustup-init.exe.sha256'
$rustupTarget = "x86_64-pc-windows-$Toolchain"
$rustupBaseUrl = "https://static.rust-lang.org/rustup/dist/$rustupTarget/rustup-init.exe"

function Test-MsvcLinker {
    if (Get-Command 'cl.exe' -ErrorAction SilentlyContinue) { return $true }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return $false }
    $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    return -not [string]::IsNullOrWhiteSpace($installation)
}

function Install-MsvcBuildTools {
    $buildToolsInstaller = Join-Path $bootstrapDirectory 'vs_BuildTools.exe'
    New-Item -ItemType Directory -Force -Path $bootstrapDirectory | Out-Null
    Write-Host 'Downloading Microsoft Visual Studio Build Tools...'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://aka.ms/vs/17/release/vs_BuildTools.exe' -OutFile $buildToolsInstaller
    $signature = Get-AuthenticodeSignature -LiteralPath $buildToolsInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw 'The Visual Studio Build Tools installer signature is invalid or is not signed by Microsoft Corporation.'
    }
    $arguments = @(
        '--quiet', '--wait', '--norestart', '--nocache',
        '--add', 'Microsoft.VisualStudio.Workload.VCTools',
        '--includeRecommended'
    )
    $process = Start-Process -FilePath $buildToolsInstaller -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Visual Studio Build Tools installation failed with exit code $($process.ExitCode)."
    }
}

function Enter-MsvcEnvironment {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ([string]::IsNullOrWhiteSpace($installation)) { throw 'Unable to locate Visual Studio Build Tools.' }
    $developerCommand = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
    & cmd.exe /d /s /c "`"$developerCommand`" -arch=x64 -host_arch=x64 >nul && set" | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') {
            Set-Item -Path "Env:$($matches[1])" -Value $matches[2]
        }
    }
}

if (-not (Test-Path -LiteralPath $cargoPath)) {
    Write-Host 'Downloading the official Rust installer...'
    New-Item -ItemType Directory -Force -Path $bootstrapDirectory | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri $rustupBaseUrl -OutFile $rustupInstaller
    Invoke-WebRequest -UseBasicParsing -Uri "$rustupBaseUrl.sha256" -OutFile $rustupChecksum
    $expectedHash = (Get-Content -Raw -LiteralPath $rustupChecksum).Trim().ToLowerInvariant()
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $rustupInstaller).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw 'The Rust installer SHA-256 checksum did not match.'
    }
    & $rustupInstaller -y --profile minimal --default-host $rustupTarget --default-toolchain "stable-$rustupTarget"
    if ($LASTEXITCODE -ne 0) { throw 'Rust installation failed.' }
}

if ($Toolchain -eq 'msvc' -and -not (Test-MsvcLinker)) {
    if ($SkipBuildToolsInstall) {
        throw 'Microsoft C++ Build Tools are missing and automatic installation is disabled.'
    }
    Install-MsvcBuildTools
    if (-not (Test-MsvcLinker)) { throw 'The MSVC C++ toolchain was not detected after Build Tools installation.' }
}

if ($Toolchain -eq 'msvc') {
    Enter-MsvcEnvironment
}

& $rustupPath toolchain install "stable-$rustupTarget" --profile minimal
if ($LASTEXITCODE -ne 0) { throw "Unable to install Rust toolchain stable-$rustupTarget." }

if (-not $SkipBuild) {
    & $rustupPath component add rustfmt clippy --toolchain "stable-$rustupTarget"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to install rustfmt and clippy.' }
    $cargoFeatures = if ($Toolchain -eq 'msvc') { @('--features', 'wasapi') } else { @('--no-default-features') }
    & $cargoPath "+stable-$rustupTarget" test --manifest-path (Join-Path $projectRoot 'native\Cargo.toml') --workspace @cargoFeatures
    if ($LASTEXITCODE -ne 0) { throw 'FoxVoice native tests failed.' }
}

Write-Host 'FoxVoice native development environment is ready.'
