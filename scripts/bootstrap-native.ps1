[CmdletBinding()]
param(
    [switch]$SkipBuild,
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

if (-not (Test-Path -LiteralPath $cargoPath)) {
    Write-Host '正在下载 Rust 官方安装器...'
    New-Item -ItemType Directory -Force -Path $bootstrapDirectory | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri $rustupBaseUrl -OutFile $rustupInstaller
    Invoke-WebRequest -UseBasicParsing -Uri "$rustupBaseUrl.sha256" -OutFile $rustupChecksum
    $expectedHash = (Get-Content -Raw -LiteralPath $rustupChecksum).Trim().ToLowerInvariant()
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $rustupInstaller).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw 'Rust 安装器 SHA-256 校验失败。'
    }
    & $rustupInstaller -y --profile minimal --default-host $rustupTarget --default-toolchain "stable-$rustupTarget"
    if ($LASTEXITCODE -ne 0) { throw 'Rust 安装失败。' }
}

if ($Toolchain -eq 'msvc' -and -not (Test-MsvcLinker)) {
    throw '缺少 Microsoft C++ Build Tools。请安装 Desktop development with C++ 工作负载后重试。正式 FoxVoice 用户不需要此开发依赖。'
}

if (-not $SkipBuild) {
    & $rustupPath component add rustfmt clippy
    if ($LASTEXITCODE -ne 0) { throw '无法安装 rustfmt 和 clippy。' }
    & $cargoPath test --manifest-path (Join-Path $projectRoot 'native\Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'FoxVoice 原生层测试失败。' }
}

Write-Host 'FoxVoice 原生开发环境已就绪。'
