using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace FoxVoice.Desktop;

public partial class MainWindow : Window
{
    private const double ProcessingBudgetMs = 112.0;
    private readonly UserSettings _settings;
    private readonly SemaphoreSlim _engineInputLock = new(1, 1);
    private Process? _audioProcess;
    private StreamWriter? _engineInput;
    private string? _supervisorPath;
    private string? _enginePath;
    private ModelItem? _selectedModel;
    private List<ModelItem> _models = [];
    private bool _stoppingAudio;
    private bool _recovering;
    private bool _ready;
    private bool _suppressDeviceSelection;

    public MainWindow()
    {
        InitializeComponent();
        _settings = UserSettings.Load();
        EmbedderPath.Text = _settings.EmbedderPath;
        F0Path.Text = _settings.F0Path;
        PitchSlider.Value = _settings.Pitch;
        OutputGainSlider.Value = _settings.OutputGainDb;
        NoiseGateToggle.IsChecked = _settings.NoiseGateEnabled;
        GameGuardToggle.IsChecked = _settings.GameGuardEnabled;
        MonitorToggle.IsEnabled = false;
        MonitorToggle.ToolTip = "当前引擎尚未接入虚拟麦克风与本地监听双输出";
        UpdateLiveControlLabels();

        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            TrySaveSettings();
            StopAudioProcess();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        await RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        FooterStatus.Text = "正在检测本机组件、音频设备与模型库…";
        try
        {
            _supervisorPath ??= FindNativeExecutable("FOXVOICE_SUPERVISOR", "foxvoice-supervisor.exe");
            _enginePath ??= FindNativeExecutable("FOXVOICE_ENGINE", "foxvoice-engine.exe");
            TopEngineText.Text = "DirectML";
            EngineBadgeText.Text = "DirectML 已就绪";
            EngineDot.Fill = (Brush)FindResource("SuccessBrush");
        }
        catch (Exception error)
        {
            TopEngineText.Text = "不可用";
            EngineBadgeText.Text = "引擎连接失败";
            EngineDot.Fill = (Brush)FindResource("DangerBrush");
            FooterStatus.Text = FriendlyError(error);
            return;
        }

        var failures = new List<string>();
        try { await RefreshDevicesAsync(); }
        catch (Exception error) { failures.Add($"音频设备：{FriendlyError(error)}"); }
        try { await RefreshModelsAsync(); }
        catch (Exception error) { failures.Add($"模型库：{FriendlyError(error)}"); }
        try { await RefreshDoctorAsync(); }
        catch (Exception error) { failures.Add($"硬件诊断：{FriendlyError(error)}"); }

        if (failures.Count == 0)
        {
            FooterStatus.Text = "本机组件、设备与模型库已就绪";
        }
        else
        {
            FooterStatus.Text = string.Join("；", failures);
        }
    }

    private async Task RefreshDevicesAsync()
    {
        using var document = JsonDocument.Parse(await RunSupervisorAsync("audio-devices"));
        var devices = document.RootElement.EnumerateArray().Select(AudioDevice.FromJson).ToList();
        var inputs = devices.Where(device => device.Direction == "input").ToList();
        var outputs = devices.Where(device => device.Direction == "output").ToList();

        _suppressDeviceSelection = true;
        InputDeviceCombo.ItemsSource = inputs;
        OutputDeviceCombo.ItemsSource = outputs;
        InputDeviceCombo.SelectedItem = inputs.FirstOrDefault(device => device.Name == _settings.InputDevice)
            ?? inputs.FirstOrDefault(device => device.IsDefault)
            ?? inputs.FirstOrDefault();
        OutputDeviceCombo.SelectedItem = outputs.FirstOrDefault(device => device.Name == _settings.OutputDevice)
            ?? outputs.FirstOrDefault(device => device.IsDefault)
            ?? outputs.FirstOrDefault();
        _suppressDeviceSelection = false;
        UpdateDeviceRouteText();

        var virtualCable = outputs.FirstOrDefault(device =>
            device.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        VirtualCableStateText.Text = virtualCable is null ? "未检测到" : "已检测到";
        VirtualCableStateText.Foreground = virtualCable is null
            ? new SolidColorBrush(Color.FromRgb(249, 200, 106))
            : (Brush)FindResource("SuccessBrush");
    }

    private async Task RefreshModelsAsync()
    {
        using var document = JsonDocument.Parse(await RunSupervisorAsync("models", "list"));
        _models = document.RootElement.EnumerateArray().Select(ModelItem.FromJson).ToList();
        ModelsList.ItemsSource = _models;
        var usable = _models.Where(model => model.IsUsable).ToList();
        PresetItems.ItemsSource = usable.Take(4).ToList();
        EmptyPresetButton.Visibility = usable.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyModelsState.Visibility = _models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var selected = _models.FirstOrDefault(model => model.Id == _settings.SelectedModelId && model.IsUsable)
            ?? usable.FirstOrDefault();
        SelectModel(selected, persist: false);
    }

    private async Task RefreshDoctorAsync()
    {
        var raw = await RunSupervisorAsync("doctor");
        DoctorText.Text = raw;
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var backend = root.GetProperty("recommendation").GetProperty("backend").GetString() ?? "windowsMl";
        RecommendationText.Text = backend switch
        {
            "tensorRt" => "TensorRT",
            "cuda" => "CUDA",
            "cpu" => "CPU 安全模式",
            _ => "WindowsML · DirectML"
        };
        var adapters = root.GetProperty("profile").GetProperty("adapters").EnumerateArray()
            .Select(value => value.GetProperty("name").GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value));
        HardwareText.Text = string.Join(" · ", adapters!);
        var allOk = root.GetProperty("checks").EnumerateArray().All(check => check.GetProperty("ok").GetBoolean());
        DoctorStateText.Text = allOk ? "自检通过" : "需要处理";
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        StudioView.Visibility = sender == StudioNav ? Visibility.Visible : Visibility.Collapsed;
        ModelsView.Visibility = sender == ModelsNav ? Visibility.Visible : Visibility.Collapsed;
        SoundboardView.Visibility = sender == SoundboardNav ? Visibility.Visible : Visibility.Collapsed;
        TrainingView.Visibility = sender == TrainingNav ? Visibility.Visible : Visibility.Collapsed;
        ComponentsView.Visibility = sender == ComponentsNav ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = sender == SettingsNav ? Visibility.Visible : Visibility.Collapsed;

        (PageEyebrow.Text, PageTitle.Text) = sender switch
        {
            RadioButton button when button == ModelsNav => ("FOXVOICE / 模型库", "管理本地声音模型"),
            RadioButton button when button == SoundboardNav => ("FOXVOICE / 音效板", "把音效安全地混入语音"),
            RadioButton button when button == TrainingNav => ("FOXVOICE / 模型训练", "本地训练与任务调度"),
            RadioButton button when button == ComponentsNav => ("FOXVOICE / 组件中心", "硬件、引擎与音频路由"),
            RadioButton button when button == SettingsNav => ("FOXVOICE / 设置", "配置 RVC 基础模型并查看诊断"),
            _ => ("FOXVOICE / 实时变声", "让声音保持在游戏里")
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshModelsAsync();
            FooterStatus.Text = "模型库已刷新";
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
    }

    private void ChooseModel_Click(object sender, RoutedEventArgs e)
    {
        ModelsNav.IsChecked = true;
        if (_selectedModel is not null) ModelsList.SelectedItem = _selectedModel;
    }

    private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelsList.SelectedItem is ModelItem model) SelectModel(model, persist: true);
    }

    private void ModelCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModelItem model }) SelectModel(model, persist: true);
    }

    private void UseModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModelItem model }) return;
        if (!model.IsUsable)
        {
            FooterStatus.Text = model.Format == "pytorchCheckpoint"
                ? "此 .pth 模型需要先转换为 ONNX"
                : ".index 已保存，但游戏实时模式不会执行检索";
            return;
        }
        SelectModel(model, persist: true);
        StudioNav.IsChecked = true;
        FooterStatus.Text = $"已选择模型：{model.DisplayName}";
    }

    private void SelectModel(ModelItem? model, bool persist)
    {
        _selectedModel = model?.IsUsable == true ? model : null;
        ActiveModelText.Text = _selectedModel?.DisplayName ?? "尚未选择";
        if (_selectedModel is not null) ModelsList.SelectedItem = _selectedModel;
        if (persist)
        {
            _settings.SelectedModelId = _selectedModel?.Id ?? "";
            TrySaveSettings();
        }
    }

    private async void ImportModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 RVC 模型或索引",
            Filter = "RVC 文件 (*.onnx;*.pth;*.index)|*.onnx;*.pth;*.index",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportModelAsync(["models", "import", dialog.FileName], "正在校验并导入本地模型…");
    }

    private async void ImportHuggingFace_Click(object sender, RoutedEventArgs e)
    {
        var url = HuggingFaceUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            FooterStatus.Text = "请输入 Hugging Face 的 HTTPS resolve 文件地址";
            return;
        }
        await ImportModelAsync(["models", "huggingface", url], "正在从 Hugging Face 下载并校验…");
    }

    private async Task ImportModelAsync(string[] arguments, string progress)
    {
        try
        {
            FooterStatus.Text = progress;
            await RunSupervisorAsync(arguments);
            await RefreshModelsAsync();
            FooterStatus.Text = "模型导入完成";
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_audioProcess is { HasExited: false })
        {
            StopAudioProcess();
            FooterStatus.Text = "实时引擎已停止";
            return;
        }
        await StartRvcAsync();
    }

    private async Task StartRvcAsync()
    {
        if (_selectedModel is null)
        {
            ModelsNav.IsChecked = true;
            FooterStatus.Text = "请先在模型库选择一个可用的 ONNX RVC 模型";
            return;
        }
        if (!File.Exists(EmbedderPath.Text) || !File.Exists(F0Path.Text))
        {
            SettingsNav.IsChecked = true;
            FooterStatus.Text = "请先配置合法的 ContentVec 与 RMVPE ONNX 文件";
            return;
        }
        try
        {
            using var resolved = JsonDocument.Parse(await RunSupervisorAsync("models", "resolve", _selectedModel.Id));
            var modelPath = resolved.RootElement.GetProperty("path").GetString()
                ?? throw new InvalidOperationException("模型路径解析失败");
            var arguments = BuildEngineArguments("rvc");
            arguments.AddRange(["--model", modelPath, "--embedder", EmbedderPath.Text, "--f0", F0Path.Text]);
            TrySaveSettings();
            StartAudioProcess(arguments.ToArray());
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
    }

    private void TestRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_audioProcess is { HasExited: false }) StopAudioProcess();
        StartAudioProcess(BuildEngineArguments("passthrough").ToArray());
    }

    private List<string> BuildEngineArguments(string command)
    {
        var arguments = new List<string>
        {
            command,
            "--pitch", PitchSlider.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            "--output-gain-db", OutputGainSlider.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        };
        if (NoiseGateToggle.IsChecked == true) arguments.Add("--noise-gate");
        if (InputDeviceCombo.SelectedItem is AudioDevice input) arguments.AddRange(["--input", input.Name]);
        if (OutputDeviceCombo.SelectedItem is AudioDevice output) arguments.AddRange(["--output", output.Name]);
        return arguments;
    }

    private void StartAudioProcess(params string[] arguments)
    {
        if (_audioProcess is { HasExited: false }) return;
        if (_enginePath is null)
        {
            FooterStatus.Text = "原生音频引擎不可用，请打开设置与诊断";
            return;
        }

        _stoppingAudio = false;
        _recovering = false;
        var startInfo = CreateStartInfo(_enginePath, arguments, redirectInput: true);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _audioProcess = process;
        process.OutputDataReceived += AudioOutputReceived;
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) Dispatcher.BeginInvoke(() => FooterStatus.Text = args.Data);
        };
        process.Exited += (_, _) => Dispatcher.BeginInvoke(async () => await HandleAudioExitedAsync(process));

        try
        {
            process.Start();
            _engineInput = process.StandardInput;
            if (GameGuardToggle.IsChecked == true)
            {
                try { process.PriorityClass = ProcessPriorityClass.High; }
                catch { process.PriorityClass = ProcessPriorityClass.AboveNormal; }
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            SetAudioRunning(true);
            FooterStatus.Text = arguments[0] == "rvc" ? "正在加载 RVC 模型…" : "正在启动安全旁路…";
        }
        catch (Exception error)
        {
            process.Dispose();
            _audioProcess = null;
            _engineInput = null;
            SetAudioRunning(false);
            FooterStatus.Text = FriendlyError(error);
        }
    }

    private async Task HandleAudioExitedAsync(Process process)
    {
        if (!ReferenceEquals(_audioProcess, process)) return;
        _audioProcess = null;
        _engineInput = null;
        process.Dispose();
        SetAudioRunning(false);
        if (!_stoppingAudio) await RecoverWithSafeBypassAsync();
    }

    private void AudioOutputReceived(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        try
        {
            using var document = JsonDocument.Parse(args.Data);
            var snapshot = EngineSnapshot.FromJson(document.RootElement);
            Dispatcher.BeginInvoke(() => ApplyEngineSnapshot(snapshot));
        }
        catch (JsonException) { }
    }

    private void ApplyEngineSnapshot(EngineSnapshot snapshot)
    {
        if (snapshot.State == "Running")
        {
            AudioStatus.Text = snapshot.Passthrough ? "安全旁路运行中" : "实时变声运行中";
            FooterStateText.Text = snapshot.Passthrough ? "安全旁路运行中" : "实时引擎运行中";
            FooterDot.Fill = (Brush)FindResource("SuccessBrush");
            FooterStatus.Text = snapshot.Message ?? "音频链路正常";
        }
        if (snapshot.OutputSampleRate > 0) SampleRateText.Text = $"{snapshot.OutputSampleRate / 1000.0:0.#} kHz";
        UnderrunText.Text = snapshot.OutputUnderruns.ToString("N0");
        StreamErrorText.Text = snapshot.StreamErrors.ToString("N0");
        if (snapshot.ProcessingUs > 0)
        {
            var milliseconds = snapshot.ProcessingUs / 1000.0;
            LatencyText.Text = $"{milliseconds:0.0} ms";
            BudgetText.Text = $"{milliseconds:0.0} / {ProcessingBudgetMs:0} ms";
            BudgetProgress.Value = Math.Clamp(milliseconds / ProcessingBudgetMs * 100.0, 0, 100);
        }
    }

    private void StopAudioProcess()
    {
        _stoppingAudio = true;
        _engineInput = null;
        if (_audioProcess is { HasExited: false })
        {
            try { _audioProcess.Kill(entireProcessTree: true); }
            catch { }
        }
        _audioProcess?.Dispose();
        _audioProcess = null;
        SetAudioRunning(false);
    }

    private async Task RecoverWithSafeBypassAsync()
    {
        if (_recovering) return;
        _recovering = true;
        FooterStatus.Text = "推理引擎已退出，正在切换安全旁路…";
        await Task.Delay(400);
        try
        {
            await RefreshDevicesAsync();
            StartAudioProcess(BuildEngineArguments("passthrough").ToArray());
            FooterStatus.Text = "已切换到安全旁路；请在诊断页查看原始错误";
        }
        catch (Exception error)
        {
            FooterStatus.Text = $"安全旁路恢复失败：{FriendlyError(error)}";
        }
        finally { _recovering = false; }
    }

    private void SetAudioRunning(bool running)
    {
        StartStopLabel.Text = running ? "停止变声" : "开始变声";
        StartStopIcon.Text = running ? "■" : "▶";
        AudioStatus.Text = running ? "正在启动…" : "已停止";
        FooterStateText.Text = running ? "引擎启动中" : "引擎待机";
        FooterDot.Fill = running ? (Brush)FindResource("SuccessBrush") : new SolidColorBrush(Color.FromRgb(89, 101, 122));
        if (!running)
        {
            SampleRateText.Text = "—";
            LatencyText.Text = "-- ms";
            BudgetText.Text = $"-- / {ProcessingBudgetMs:0} ms";
            BudgetProgress.Value = 0;
        }
    }

    private async void LiveControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateLiveControlLabels();
        _settings.Pitch = PitchSlider.Value;
        _settings.OutputGainDb = OutputGainSlider.Value;
        _settings.NoiseGateEnabled = NoiseGateToggle.IsChecked == true;
        TrySaveSettings();
        await SendLiveParametersAsync();
    }

    private void UpdateLiveControlLabels()
    {
        PitchValueText.Text = $"{PitchSlider.Value:+0.0;-0.0;0.0}";
        OutputGainValueText.Text = $"{OutputGainSlider.Value:+0;-0;0} dB";
    }

    private async Task SendLiveParametersAsync()
    {
        if (_audioProcess is not { HasExited: false } || _engineInput is null) return;
        var command = JsonSerializer.Serialize(new
        {
            type = "live",
            pitch = PitchSlider.Value,
            outputGainDb = OutputGainSlider.Value,
            noiseGateEnabled = NoiseGateToggle.IsChecked == true
        });
        await _engineInputLock.WaitAsync();
        try
        {
            await _engineInput.WriteLineAsync(command);
            await _engineInput.FlushAsync();
        }
        catch (IOException) { }
        finally { _engineInputLock.Release(); }
    }

    private void GameGuard_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var enabled = GameGuardToggle.IsChecked == true;
        GuardStateText.Text = enabled ? "资源保护已启用" : "资源保护已关闭";
        _settings.GameGuardEnabled = enabled;
        if (_audioProcess is { HasExited: false })
        {
            try { _audioProcess.PriorityClass = enabled ? ProcessPriorityClass.High : ProcessPriorityClass.Normal; }
            catch { }
        }
        TrySaveSettings();
    }

    private void Monitor_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || MonitorToggle.IsChecked != true) return;
        MonitorToggle.IsChecked = false;
        FooterStatus.Text = "当前引擎只有一个输出总线；双输出监听会在音频路由阶段实现";
    }

    private void OpenDevices_Click(object sender, RoutedEventArgs e) => DeviceOverlay.Visibility = Visibility.Visible;
    private void CloseDevices_Click(object sender, RoutedEventArgs e) => DeviceOverlay.Visibility = Visibility.Collapsed;
    private void OverlayBackground_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DeviceOverlay.Visibility = Visibility.Collapsed;
    private void OverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void SaveDevices_Click(object sender, RoutedEventArgs e)
    {
        TrySaveSettings();
        UpdateDeviceRouteText();
        DeviceOverlay.Visibility = Visibility.Collapsed;
        FooterStatus.Text = _audioProcess is { HasExited: false }
            ? "设备选择已保存，将在下次启动引擎时生效"
            : "设备选择已保存";
    }

    private void DeviceSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressDeviceSelection) return;
        UpdateDeviceRouteText();
    }

    private void UpdateDeviceRouteText()
    {
        InputRouteText.Text = (InputDeviceCombo.SelectedItem as AudioDevice)?.Name ?? "没有输入设备";
        OutputRouteText.Text = (OutputDeviceCombo.SelectedItem as AudioDevice)?.Name ?? "没有输出设备";
    }

    private void BrowseEmbedder_Click(object sender, RoutedEventArgs e) => BrowseOnnxInto(EmbedderPath, "选择 ContentVec ONNX");
    private void BrowseF0_Click(object sender, RoutedEventArgs e) => BrowseOnnxInto(F0Path, "选择 RMVPE ONNX");

    private void BrowseOnnxInto(TextBox target, string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = "ONNX 模型 (*.onnx)|*.onnx" };
        if (dialog.ShowDialog(this) != true) return;
        target.Text = dialog.FileName;
        TrySaveSettings();
    }

    private void TrySaveSettings()
    {
        try
        {
            _settings.EmbedderPath = EmbedderPath.Text;
            _settings.F0Path = F0Path.Text;
            _settings.InputDevice = (InputDeviceCombo.SelectedItem as AudioDevice)?.Name ?? "";
            _settings.OutputDevice = (OutputDeviceCombo.SelectedItem as AudioDevice)?.Name ?? "";
            _settings.SelectedModelId = _selectedModel?.Id ?? _settings.SelectedModelId;
            _settings.Pitch = PitchSlider.Value;
            _settings.OutputGainDb = OutputGainSlider.Value;
            _settings.NoiseGateEnabled = NoiseGateToggle.IsChecked == true;
            _settings.GameGuardEnabled = GameGuardToggle.IsChecked == true;
            _settings.Save();
        }
        catch (Exception error)
        {
            FooterStatus.Text = $"设置保存失败：{FriendlyError(error)}";
        }
    }

    private async Task<string> RunSupervisorAsync(params string[] arguments)
    {
        if (_supervisorPath is null) throw new InvalidOperationException("原生控制服务尚未连接");
        using var process = new Process { StartInfo = CreateStartInfo(_supervisorPath, arguments, redirectInput: false) };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "控制服务执行失败" : error.Trim());
        return output;
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments, bool redirectInput)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static string FindNativeExecutable(string environmentName, string fileName)
    {
        var configured = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var bundled = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(bundled)) return bundled;
        try { return NativeBundle.EnsureExtracted(fileName); }
        catch (FileNotFoundException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var profile in new[] { "release", "debug" })
            {
                var candidate = Path.Combine(directory.FullName, "native", "target", profile, fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException($"找不到 {fileName}。请重新运行 FoxVoice 发布脚本。", fileName);
    }

    private static string FriendlyError(Exception error)
    {
        var message = error.Message.Replace("foxvoice-supervisor:", "").Replace("foxvoice-engine:", "").Trim();
        return string.IsNullOrWhiteSpace(message) ? "发生未知错误，请查看诊断日志" : message;
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (e.ClickCount == 2) ToggleMaximize();
            else DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private sealed record AudioDevice(string Id, string Name, string Direction, bool IsDefault)
    {
        public static AudioDevice FromJson(JsonElement value) => new(
            value.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            value.GetProperty("name").GetString() ?? "未知设备",
            value.GetProperty("direction").GetString() ?? "",
            value.GetProperty("isDefault").GetBoolean());
    }

    private sealed record ModelItem(string Id, string DisplayName, string Format, string State, long SizeBytes, string Hash)
    {
        public bool IsUsable => Format == "onnx" && State == "ready";
        public string SizeText => $"{SizeBytes / 1024d / 1024d:N1} MB";
        public string ShortHash => Hash.Length > 16 ? Hash[..16] : Hash;
        public string FormatLabel => Format switch { "onnx" => "ONNX", "pytorchCheckpoint" => "PyTorch", "faissIndex" => "FAISS index", _ => Format };
        public string StateLabel => State switch { "ready" => "可使用", "conversionRequired" => "需要转换", "storedOnly" => "仅保存", _ => State };
        public static ModelItem FromJson(JsonElement value) => new(
            value.GetProperty("id").GetString() ?? "",
            value.GetProperty("displayName").GetString() ?? "未命名",
            value.GetProperty("format").GetString() ?? "",
            value.GetProperty("state").GetString() ?? "",
            value.GetProperty("sizeBytes").GetInt64(),
            value.GetProperty("sha256").GetString() ?? "");
    }

    private sealed record EngineSnapshot(
        string? State,
        string? Message,
        bool Passthrough,
        int OutputSampleRate,
        ulong ProcessingUs,
        ulong OutputUnderruns,
        ulong StreamErrors)
    {
        public static EngineSnapshot FromJson(JsonElement root)
        {
            var eventName = root.TryGetProperty("event", out var eventValue) ? eventValue.GetString() : null;
            var state = root.TryGetProperty("state", out var stateValue) ? stateValue.GetString() : eventName == "audioStarted" ? "Running" : null;
            var message = root.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            var sampleRate = root.TryGetProperty("outputSampleRate", out var rateValue) ? rateValue.GetInt32()
                : root.TryGetProperty("sampleRate", out rateValue) ? rateValue.GetInt32() : 0;
            var processing = root.TryGetProperty("processingUs", out var processingValue) ? processingValue.GetUInt64() : 0;
            var underruns = root.TryGetProperty("outputUnderruns", out var underrunValue) ? underrunValue.GetUInt64() : 0;
            var streamErrors = root.TryGetProperty("streamErrors", out var errorValue) ? errorValue.GetUInt64() : 0;
            var passthrough = eventName == "audioStarted"
                || (root.TryGetProperty("passthrough", out var passthroughValue) && passthroughValue.GetBoolean())
                || (root.TryGetProperty("mode", out var modeValue) && modeValue.GetString() == "safeBypass");
            return new(state, message, passthrough, sampleRate, processing, underruns, streamErrors);
        }
    }
}
