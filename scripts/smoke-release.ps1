[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$releaseDirectory = Join-Path $projectRoot 'artifacts\FoxVoice-win-x64'
$desktop = Join-Path $releaseDirectory 'FoxVoice.exe'
$supervisor = Join-Path $releaseDirectory 'foxvoice-supervisor.exe'
$engine = Join-Path $releaseDirectory 'foxvoice-engine.exe'

foreach ($path in @($desktop, $supervisor, $engine)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing release file: $path" }
}

$manifest = Get-Content -Raw -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.json') | ConvertFrom-Json
foreach ($entry in $manifest) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $releaseDirectory $entry.File)).Hash
    if ($actual -ne $entry.SHA256) { throw "SHA-256 mismatch: $($entry.File)" }
}

$doctor = & $supervisor doctor | ConvertFrom-Json
if (-not $doctor.profile) { throw 'Doctor output has no hardware profile.' }
$devices = & $supervisor audio-devices | ConvertFrom-Json
if (@($devices).Count -eq 0) { throw 'No audio devices were returned.' }
$models = & $supervisor models list | ConvertFrom-Json

$smokeId = [guid]::NewGuid().ToString('N')
$stdout = Join-Path ([IO.Path]::GetTempPath()) "FoxVoiceEngine-$smokeId.stdout"
$stderr = Join-Path ([IO.Path]::GetTempPath()) "FoxVoiceEngine-$smokeId.stderr"
$audio = Start-Process -FilePath $engine -ArgumentList 'passthrough' -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    $running = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($audio.HasExited) { break }
        $running = Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue |
            ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } |
            Where-Object state -eq 'Running' | Select-Object -First 1
        if ($running) { break }
    }
    if (-not $running) {
        $errorText = Get-Content -Raw -LiteralPath $stderr -ErrorAction SilentlyContinue
        throw "Realtime engine did not reach Running state. $errorText"
    }
}
finally {
    if (-not $audio.HasExited) {
        Stop-Process -Id $audio.Id -Force
        $audio.WaitForExit(5000) | Out-Null
    }
    $audio.Dispose()
    foreach ($temporaryFile in @($stdout, $stderr)) {
        if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -Force }
    }
}

$ui = Start-Process -FilePath $desktop -WorkingDirectory $releaseDirectory -PassThru
try {
    $uiReady = $false
    $lastTitle = ''
    $lastResponding = $false
    $lastHandle = 0
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        $ui.Refresh()
        if ($ui.HasExited) { break }
        $lastTitle = $ui.MainWindowTitle
        $lastResponding = $ui.Responding
        $lastHandle = $ui.MainWindowHandle
        if ($lastResponding -and $lastHandle -ne 0 -and $lastTitle.EndsWith('FoxVoice', [StringComparison]::Ordinal)) {
            $uiReady = $true
            break
        }
    }
    if (-not $uiReady) {
        $exitDetail = if ($ui.HasExited) { " Exit code: $($ui.ExitCode)." } else { '' }
        throw "Desktop UI did not become alive and responsive within 15 seconds.$exitDetail Last title='$lastTitle', responding=$lastResponding, handle=$lastHandle."
    }
}
finally {
    if (-not $ui.HasExited) { Stop-Process -Id $ui.Id -Force }
    $ui.Dispose()
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$singleExeDirectory = Join-Path $tempRoot "FoxVoiceSingleExe-$smokeId"
$singleExeFullPath = [IO.Path]::GetFullPath($singleExeDirectory)
if (-not $singleExeFullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($singleExeFullPath)).StartsWith('FoxVoiceSingleExe-', [StringComparison]::Ordinal)) {
    throw "Unsafe single-EXE smoke directory: $singleExeFullPath"
}
$isolatedRuntime = Join-Path $singleExeFullPath 'runtime'
New-Item -ItemType Directory -Path $singleExeFullPath | Out-Null
$isolatedDesktop = Join-Path $singleExeFullPath 'FoxVoice.exe'
Copy-Item -LiteralPath $desktop -Destination $isolatedDesktop
$isolatedStart = [Diagnostics.ProcessStartInfo]::new($isolatedDesktop)
$isolatedStart.UseShellExecute = $false
$isolatedStart.WorkingDirectory = $singleExeFullPath
$isolatedStart.Environment['FOXVOICE_RUNTIME_DIR'] = $isolatedRuntime
$isolatedUi = [Diagnostics.Process]::Start($isolatedStart)
try {
    $isolatedReady = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        $isolatedUi.Refresh()
        if ($isolatedUi.HasExited) { break }
        if ($isolatedUi.Responding -and $isolatedUi.MainWindowHandle -ne 0 -and
            $isolatedUi.MainWindowTitle.EndsWith('FoxVoice', [StringComparison]::Ordinal)) {
            $isolatedReady = $true
            break
        }
    }
    if (-not $isolatedReady) { throw 'Isolated single-EXE UI did not start.' }
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ((Test-Path -LiteralPath (Join-Path $isolatedRuntime 'foxvoice-supervisor.exe')) -and
            (Test-Path -LiteralPath (Join-Path $isolatedRuntime 'foxvoice-engine.exe'))) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath (Join-Path $isolatedRuntime 'foxvoice-supervisor.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $isolatedRuntime 'foxvoice-engine.exe'))) {
        throw 'Embedded native components were not extracted in isolated single-EXE mode.'
    }
}
finally {
    if (-not $isolatedUi.HasExited) {
        Stop-Process -Id $isolatedUi.Id -Force
        $isolatedUi.WaitForExit(5000) | Out-Null
    }
    $isolatedUi.Dispose()
    $isolatedRuntimeRoot = [IO.Path]::GetFullPath($isolatedRuntime).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Get-CimInstance Win32_Process -Filter "Name='foxvoice-supervisor.exe' OR Name='foxvoice-engine.exe'" |
        Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($isolatedRuntimeRoot, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $singleExeFullPath) {
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $singleExeFullPath -Recurse -Force
                break
            }
            catch {
                if ($attempt -eq 19) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
    }
}

Write-Host "FoxVoice release smoke test passed: $(@($devices).Count) devices, $(@($models).Count) models, isolated single-EXE startup verified."
