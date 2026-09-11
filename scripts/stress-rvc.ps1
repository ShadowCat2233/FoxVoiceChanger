[CmdletBinding()]
param(
    [ValidateRange(0.05, 1440.0)]
    [double]$DurationMinutes = 240,
    [ValidateRange(0, 3600)]
    [int]$WarmupSeconds = 30,
    [ValidateRange(0, 3600)]
    [int]$GuardCycleSeconds = 0,
    [ValidateRange(0, 1000000)]
    [long]$MaxNewUnderruns = 0,
    [ValidateRange(0, 1000000)]
    [long]$MaxTransitionUnderruns = 16,
    [ValidateRange(0, 1000000)]
    [long]$MaxNewOverruns = 0,
    [ValidateRange(0, 1000000)]
    [long]$MaxNewDroppedSamples = 0,
    [string]$ReportPath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$releaseRoot = Join-Path $projectRoot 'artifacts\FoxVoice-win-x64'
$engine = Join-Path $releaseRoot 'foxvoice-engine.exe'
$supervisor = Join-Path $releaseRoot 'foxvoice-supervisor.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'FoxVoice\settings.json'

foreach ($path in @($engine, $supervisor, $settingsPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required file is missing: $path" }
}
$settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($settings.SelectedModelId)) { throw 'FoxVoice setting SelectedModelId is empty.' }
$foundation = @(& $supervisor foundation-models status | ConvertFrom-Json)
if ([string]::IsNullOrWhiteSpace($settings.EmbedderPath)) {
    $component = $foundation | Where-Object { $_.id -eq 'contentvec' -and $_.verified } | Select-Object -First 1
    if ($component) { $settings.EmbedderPath = $component.path }
}
if ([string]::IsNullOrWhiteSpace($settings.F0Path)) {
    $component = $foundation | Where-Object { $_.id -eq 'rmvpe' -and $_.verified } | Select-Object -First 1
    if ($component) { $settings.F0Path = $component.path }
}
foreach ($property in @('EmbedderPath', 'F0Path')) {
    if ([string]::IsNullOrWhiteSpace($settings.$property)) { throw "No verified $property model is configured or installed." }
}
foreach ($path in @($settings.EmbedderPath, $settings.F0Path)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Configured model file is missing: $path" }
}
$resolved = & $supervisor models resolve $settings.SelectedModelId | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $resolved.path -PathType Leaf)) {
    throw 'The selected Generator could not be resolved.'
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportDirectory = Join-Path $projectRoot 'artifacts\stress-reports'
    New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    $ReportPath = Join-Path $reportDirectory "rvc-stress-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
}
else {
    $ReportPath = [IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = Split-Path -Parent $ReportPath
    if (-not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
        throw "Report directory does not exist: $reportDirectory"
    }
}

$arguments = [Collections.Generic.List[string]]::new()
foreach ($argument in @('rvc', '--provider', $(if ($settings.PreferredProvider -eq 'nvtrtx') { 'nvtrtx' } else { 'directml' }),
        '--model', [string]$resolved.path, '--embedder', [string]$settings.EmbedderPath,
        '--f0', [string]$settings.F0Path, '--pitch', ([double]$settings.Pitch).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture),
        '--output-gain-db', ([double]$settings.OutputGainDb).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture))) {
    $arguments.Add($argument)
}
if ($settings.NoiseGateEnabled) { $arguments.Add('--noise-gate') }
if (-not [string]::IsNullOrWhiteSpace($settings.InputDevice)) { $arguments.Add('--input'); $arguments.Add($settings.InputDevice) }
if (-not [string]::IsNullOrWhiteSpace($settings.OutputDevice)) { $arguments.Add('--output'); $arguments.Add($settings.OutputDevice) }

$start = [Diagnostics.ProcessStartInfo]::new($engine)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardOutputEncoding = [Text.Encoding]::UTF8
$start.StandardErrorEncoding = [Text.Encoding]::UTF8
foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }

$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$startedAt = [DateTimeOffset]::Now
$deadline = $startedAt.AddMinutes($DurationMinutes)
$effectiveWarmupSeconds = [Math]::Min($WarmupSeconds, [Math]::Floor($DurationMinutes * 60 / 3))
$warmupEnds = $startedAt.AddSeconds($effectiveWarmupSeconds)
$samples = [Collections.Generic.List[object]]::new()
$allStates = [Collections.Generic.HashSet[string]]::new()
$profiles = [Collections.Generic.HashSet[string]]::new()
$unexpectedExit = $false
$nextGuardChange = if ($GuardCycleSeconds -gt 0) { $startedAt.AddSeconds($GuardCycleSeconds) } else { [DateTimeOffset]::MaxValue }
$guardSequence = @('stable', 'survival', 'normal')
$guardIndex = 0

try {
    if (-not $process.Start()) { throw 'Failed to start the RVC engine.' }
    try { $process.PriorityClass = [Diagnostics.ProcessPriorityClass]::High } catch { }
    $stderrTask = $process.StandardError.ReadToEndAsync()
    while ([DateTimeOffset]::Now -lt $deadline) {
        if ($process.HasExited) { $unexpectedExit = $true; break }
        if ($allStates.Contains('Running') -and [DateTimeOffset]::Now -ge $nextGuardChange) {
            $level = $guardSequence[$guardIndex % $guardSequence.Count]
            $process.StandardInput.WriteLine((@{ type = 'guard'; level = $level } | ConvertTo-Json -Compress))
            $process.StandardInput.Flush()
            $guardIndex++
            $nextGuardChange = [DateTimeOffset]::Now.AddSeconds($GuardCycleSeconds)
        }
        $line = $process.StandardOutput.ReadLine()
        if ($null -eq $line) { $unexpectedExit = $true; break }
        try { $sample = $line | ConvertFrom-Json } catch { continue }
        if ($sample.event -ne 'engineStatus') { continue }
        [void]$allStates.Add([string]$sample.state)
        [void]$profiles.Add([string]$sample.guardProfile)
        if ([DateTimeOffset]::Now -ge $warmupEnds) { $samples.Add($sample) }
        Write-Progress -Activity 'FoxVoice RVC stress test' -Status "$($sample.state), $($sample.guardProfile), $([Math]::Round(($deadline - [DateTimeOffset]::Now).TotalMinutes, 1)) min remaining" -PercentComplete ([Math]::Min(100, 100 * ([DateTimeOffset]::Now - $startedAt).TotalMilliseconds / ($deadline - $startedAt).TotalMilliseconds))
    }
}
finally {
    Write-Progress -Activity 'FoxVoice RVC stress test' -Completed
    if (-not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit(5000) | Out-Null
    }
}

$stderr = if ($stderrTask) { $stderrTask.GetAwaiter().GetResult().Trim() } else { '' }
$endedAt = [DateTimeOffset]::Now
if ($samples.Count -eq 0) { throw "No post-warmup engine telemetry was collected. $stderr" }
$processingMs = @($samples | ForEach-Object { [double]$_.processingUs / 1000 }) | Sort-Object
$p99Index = [Math]::Min($processingMs.Count - 1, [Math]::Max(0, [Math]::Ceiling($processingMs.Count * 0.99) - 1))
$underruns = 0L
$transitionUnderruns = 0L
$overruns = 0L
$dropped = 0L
$counterResets = 0
$transitionCooldown = 0
for ($index = 1; $index -lt $samples.Count; $index++) {
    $previous = $samples[$index - 1]
    $current = $samples[$index]
    if ([string]$current.guardProfile -ne [string]$previous.guardProfile) { $transitionCooldown = 2 }
    $beforeUnderruns = [long]$previous.outputUnderruns
    $afterUnderruns = [long]$current.outputUnderruns
    if ($afterUnderruns -ge $beforeUnderruns) {
        $delta = $afterUnderruns - $beforeUnderruns
        if ($transitionCooldown -gt 0) { $transitionUnderruns += $delta } else { $underruns += $delta }
    }
    else { $counterResets++ }
    if ($transitionCooldown -gt 0) { $transitionCooldown-- }
    foreach ($counter in @(
            @{ Name = 'inputOverruns'; Target = 'overruns' },
            @{ Name = 'outputDroppedSamples'; Target = 'dropped' })) {
        $before = [long]$previous.($counter.Name)
        $after = [long]$current.($counter.Name)
        if ($after -ge $before) {
            Set-Variable -Name $counter.Target -Value ((Get-Variable -Name $counter.Target -ValueOnly) + $after - $before)
        }
        else { $counterResets++ }
    }
}
$maxBudgetRatio = ($samples | ForEach-Object { ([double]$_.processingUs / 1000) / [double]$_.chunkMs } | Measure-Object -Maximum).Maximum
$failures = [Collections.Generic.List[string]]::new()
if ($unexpectedExit) { $failures.Add('Engine exited before the requested duration.') }
if ($allStates.Contains('Error')) { $failures.Add('Engine reported Error state.') }
if (-not $allStates.Contains('Running')) { $failures.Add('Engine never reported Running state.') }
if ($underruns -gt $MaxNewUnderruns) { $failures.Add("New output underruns $underruns exceeded $MaxNewUnderruns.") }
if ($transitionUnderruns -gt $MaxTransitionUnderruns) { $failures.Add("Transition-window output underruns $transitionUnderruns exceeded $MaxTransitionUnderruns.") }
if ($overruns -gt $MaxNewOverruns) { $failures.Add("New input overruns $overruns exceeded $MaxNewOverruns.") }
if ($dropped -gt $MaxNewDroppedSamples) { $failures.Add("New dropped samples $dropped exceeded $MaxNewDroppedSamples.") }
if ([double]$maxBudgetRatio -ge 0.7) { $failures.Add("Maximum processing/budget ratio $([Math]::Round($maxBudgetRatio, 3)) was not below 0.7.") }

$report = [ordered]@{
    schemaVersion = 1
    passed = $failures.Count -eq 0
    startedAt = $startedAt.ToString('o')
    endedAt = $endedAt.ToString('o')
    requestedDurationMinutes = $DurationMinutes
    measuredSeconds = [Math]::Round(($endedAt - $warmupEnds).TotalSeconds, 1)
    warmupSeconds = $effectiveWarmupSeconds
    modelId = $settings.SelectedModelId
    provider = $(if ($settings.PreferredProvider -eq 'nvtrtx') { 'nvtrtx' } else { 'directml' })
    inputDevice = $settings.InputDevice
    outputDevice = $settings.OutputDevice
    guardCycleSeconds = $GuardCycleSeconds
    observedStates = @($allStates)
    observedGuardProfiles = @($profiles)
    telemetrySamples = $samples.Count
    averageProcessingMs = [Math]::Round(($processingMs | Measure-Object -Average).Average, 3)
    p99ProcessingMs = [Math]::Round($processingMs[$p99Index], 3)
    maximumBudgetRatio = [Math]::Round([double]$maxBudgetRatio, 4)
    newOutputUnderruns = $underruns
    transitionOutputUnderruns = $transitionUnderruns
    newInputOverruns = $overruns
    newDroppedSamples = $dropped
    telemetryCounterResets = $counterResets
    failures = @($failures)
    stderr = $stderr
}
$temporaryReport = "$ReportPath.tmp"
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporaryReport -Encoding utf8
Move-Item -LiteralPath $temporaryReport -Destination $ReportPath -Force
$process.Dispose()

Write-Host "FoxVoice RVC stress report: $ReportPath"
$report | ConvertTo-Json -Depth 5
if (-not $report.passed) { exit 1 }
