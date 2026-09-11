[CmdletBinding()]
param(
    [ValidateRange(0.1, 240.0)]
    [double]$DurationMinutes = 10,
    [ValidateRange(0, 3600)]
    [int]$WarmupSeconds = 15,
    [ValidateRange(0, 1000000)]
    [long]$MaxPrimaryUnderruns = 0,
    [ValidateRange(0, 1000000)]
    [long]$MaxMonitorUnderruns = 0,
    [string]$ReportPath = '',
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$releaseRoot = Join-Path $projectRoot 'artifacts\FoxVoice-win-x64'
$engine = Join-Path $releaseRoot 'foxvoice-engine.exe'
$supervisor = Join-Path $releaseRoot 'foxvoice-supervisor.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'FoxVoice\settings.json'
foreach ($path in @($engine, $supervisor, $settingsPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file is missing: $path" }
}

$settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
$devices = @(& $supervisor audio-devices | ConvertFrom-Json)
$virtualCapture = $devices | Where-Object {
    $_.direction -eq 'input' -and ($_.name -match '(?i)CABLE|Virtual')
} | Select-Object -First 1
$virtualRender = $devices | Where-Object {
    $_.direction -eq 'output' -and $_.name -match '(?i)CABLE Input|CABLE In|Virtual'
} | Sort-Object @{ Expression = { if ($_.name -match '(?i)^CABLE Input') { 0 } else { 1 } } } | Select-Object -First 1
$physicalOutputs = @($devices | Where-Object {
    $_.direction -eq 'output' -and $_.name -notmatch '(?i)CABLE|Virtual'
})
$monitorOutput = $physicalOutputs | Where-Object { $_.name -eq $settings.MonitorOutputDevice } | Select-Object -First 1
if (-not $monitorOutput) { $monitorOutput = $physicalOutputs | Where-Object isDefault | Select-Object -First 1 }
if (-not $monitorOutput) { $monitorOutput = $physicalOutputs | Select-Object -First 1 }
$primaryInput = $devices | Where-Object { $_.direction -eq 'input' -and $_.name -eq $settings.InputDevice } | Select-Object -First 1
if (-not $primaryInput) { $primaryInput = $devices | Where-Object { $_.direction -eq 'input' -and $_.isDefault } | Select-Object -First 1 }
if (-not $primaryInput) { $primaryInput = $devices | Where-Object { $_.direction -eq 'input' -and $_.name -notmatch '(?i)CABLE|Virtual' } | Select-Object -First 1 }
if (-not $virtualCapture -or -not $virtualRender -or -not $monitorOutput -or -not $primaryInput) {
    throw 'Dual-output test requires a virtual render endpoint, its virtual capture endpoint, and one physical output.'
}

$foundation = @(& $supervisor foundation-models status | ConvertFrom-Json)
$embedder = [string]$settings.EmbedderPath
$f0 = [string]$settings.F0Path
if ([string]::IsNullOrWhiteSpace($embedder)) {
    $embedder = [string](($foundation | Where-Object { $_.id -eq 'contentvec' -and $_.verified } | Select-Object -First 1).path)
}
if ([string]::IsNullOrWhiteSpace($f0)) {
    $f0 = [string](($foundation | Where-Object { $_.id -eq 'rmvpe' -and $_.verified } | Select-Object -First 1).path)
}
if ([string]::IsNullOrWhiteSpace($settings.SelectedModelId)) { throw 'No Generator is selected in FoxVoice settings.' }
$model = & $supervisor models resolve $settings.SelectedModelId | ConvertFrom-Json
foreach ($path in @([string]$model.path, $embedder, $f0)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required model is missing: $path" }
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportDirectory = Join-Path $projectRoot 'artifacts\stress-reports'
    New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    $ReportPath = Join-Path $reportDirectory "dual-output-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
}
else {
    $ReportPath = [IO.Path]::GetFullPath($ReportPath)
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $ReportPath) -PathType Container)) {
        throw "Report directory does not exist: $(Split-Path -Parent $ReportPath)"
    }
}

function New-FoxVoiceProcess([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($engine)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Failed to start FoxVoice engine.' }
    return $process
}

$provider = if ($settings.PreferredProvider -eq 'nvtrtx') { 'nvtrtx' } else { 'directml' }
$primaryArguments = @(
    'rvc', '--provider', $provider, '--model', [string]$model.path, '--embedder', $embedder, '--f0', $f0,
    '--pitch', ([double]$settings.Pitch).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture),
    '--output-gain-db', ([double]$settings.OutputGainDb).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture),
    '--input', [string]$primaryInput.name, '--output', [string]$virtualRender.name
)
if ($settings.NoiseGateEnabled) { $primaryArguments += '--noise-gate' }
$monitorArguments = @('passthrough', '--monitor', '--input', [string]$virtualCapture.name, '--output', [string]$monitorOutput.name)

if ($ValidateOnly) {
    [ordered]@{
        ready = $true
        provider = $provider
        model = [string]$model.path
        embedder = $embedder
        f0 = $f0
        primaryInput = $primaryInput.name
        virtualRender = $virtualRender.name
        virtualCapture = $virtualCapture.name
        monitorOutput = $monitorOutput.name
    } | ConvertTo-Json -Depth 3
    return
}

$primary = $null
$monitor = $null
$primarySamples = [Collections.Generic.List[object]]::new()
$monitorSamples = [Collections.Generic.List[object]]::new()
$startedAt = [DateTimeOffset]::Now
$deadline = $startedAt.AddMinutes($DurationMinutes)
$warmupEnds = $startedAt.AddSeconds([Math]::Min($WarmupSeconds, $DurationMinutes * 20))
try {
    $primary = New-FoxVoiceProcess $primaryArguments
    try { $primary.PriorityClass = [Diagnostics.ProcessPriorityClass]::High } catch { }
    $monitor = New-FoxVoiceProcess $monitorArguments
    try { $monitor.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal } catch { }
    $primaryError = $primary.StandardError.ReadToEndAsync()
    $monitorError = $monitor.StandardError.ReadToEndAsync()
    $primaryRead = $primary.StandardOutput.ReadLineAsync()
    $monitorRead = $monitor.StandardOutput.ReadLineAsync()
    while ([DateTimeOffset]::Now -lt $deadline) {
        if ($primary.HasExited -or $monitor.HasExited) { break }
        $completed = [Threading.Tasks.Task]::WhenAny($primaryRead, $monitorRead).GetAwaiter().GetResult()
        $line = $completed.GetAwaiter().GetResult()
        if ($completed -eq $primaryRead) {
            if ($null -eq $line) { break }
            try { $sample = $line | ConvertFrom-Json } catch { $sample = $null }
            if ($sample -and $sample.event -eq 'engineStatus' -and [DateTimeOffset]::Now -ge $warmupEnds) { $primarySamples.Add($sample) }
            $primaryRead = $primary.StandardOutput.ReadLineAsync()
        }
        else {
            if ($null -eq $line) { break }
            try { $sample = $line | ConvertFrom-Json } catch { $sample = $null }
            if ($sample -and $sample.event -eq 'engineStatus' -and [DateTimeOffset]::Now -ge $warmupEnds) { $monitorSamples.Add($sample) }
            $monitorRead = $monitor.StandardOutput.ReadLineAsync()
        }
    }
    if ($primary.HasExited -or $monitor.HasExited) {
        throw "A dual-output process exited early. Primary: $($primaryError.GetAwaiter().GetResult()) Monitor: $($monitorError.GetAwaiter().GetResult())"
    }
}
finally {
    foreach ($process in @($primary, $monitor)) {
        if ($null -eq $process) { continue }
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}

if ($primarySamples.Count -eq 0 -or $monitorSamples.Count -eq 0) { throw 'Dual-output test did not collect steady-state telemetry from both processes.' }
foreach ($sample in @($primarySamples) + @($monitorSamples)) {
    if ($sample.state -ne 'Running') { throw "Dual-output process entered state $($sample.state)." }
}
$primaryUnderruns = [long]$primarySamples[-1].outputUnderruns - [long]$primarySamples[0].outputUnderruns
$monitorUnderruns = [long]$monitorSamples[-1].outputUnderruns - [long]$monitorSamples[0].outputUnderruns
$primaryOverruns = [long]$primarySamples[-1].inputOverruns - [long]$primarySamples[0].inputOverruns
$monitorOverruns = [long]$monitorSamples[-1].inputOverruns - [long]$monitorSamples[0].inputOverruns
$primaryDropped = [long]$primarySamples[-1].outputDroppedSamples - [long]$primarySamples[0].outputDroppedSamples
$monitorDropped = [long]$monitorSamples[-1].outputDroppedSamples - [long]$monitorSamples[0].outputDroppedSamples
$passed = $primaryUnderruns -le $MaxPrimaryUnderruns -and $monitorUnderruns -le $MaxMonitorUnderruns -and
    $primaryOverruns -eq 0 -and $monitorOverruns -eq 0 -and $primaryDropped -eq 0 -and $monitorDropped -eq 0
$report = [ordered]@{
    passed = $passed
    durationMinutes = $DurationMinutes
    primary = [ordered]@{ input = $primaryInput.name; output = $virtualRender.name; samples = $primarySamples.Count; underruns = $primaryUnderruns; overruns = $primaryOverruns; droppedSamples = $primaryDropped }
    monitor = [ordered]@{ input = $virtualCapture.name; output = $monitorOutput.name; samples = $monitorSamples.Count; underruns = $monitorUnderruns; overruns = $monitorOverruns; droppedSamples = $monitorDropped }
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
$report | ConvertTo-Json -Depth 5
if (-not $passed) { throw "Dual-output stability thresholds failed. Report: $ReportPath" }
Write-Host "FoxVoice dual-output stress test passed. Report: $ReportPath"
