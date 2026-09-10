using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FoxVoice.Desktop;

public partial class MainWindow : Window
{
    private const double DefaultProcessingBudgetMs = 160.0;
    private readonly UserSettings _settings;
    private readonly SemaphoreSlim _engineInputLock = new(1, 1);
    private readonly DispatcherTimer _deviceRefreshTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private Process? _audioProcess;
    private Process? _monitorProcess;
    private StreamWriter? _engineInput;
    private string? _supervisorPath;
    private string? _enginePath;
    private string? _converterPath;
    private ModelItem? _selectedModel;
    private List<ModelItem> _models = [];
    private bool _stoppingAudio;
    private bool _recovering;
    private bool _ready;
    private bool _suppressDeviceSelection;
    private string _guardProfile = "normal";
    private int _guardOverloadStreak;
    private int _guardRecoveryStreak;
    private ulong _guardLastUnderruns;
    private ulong _guardLastStreamErrors;
    private bool _guardCommandPending;
    private bool _refreshingDevices;
    private AudioDevice? _virtualCableInput;
    private readonly ComboBox _monitorDeviceCombo = new() { DisplayMemberPath = "Name", Margin = new Thickness(0, 6, 0, 12) };
    private readonly TextBlock _foundationStateText = new() { Text = "检测中", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _installFoundationButton = new() { Content = "安装基础模型", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _rvcSelfTestButton = new() { Content = "运行 RVC 三模型自检", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };

    public MainWindow()
    {
        InitializeComponent();
        AddFoundationModelsCard();
        AddMonitorDevicePicker();
        AddVirtualCableInstallAction();
        AddRvcSelfTestAction();
        _settings = UserSettings.Load();
        EmbedderPath.Text = _settings.EmbedderPath;
        F0Path.Text = _settings.F0Path;
        PitchSlider.Value = _settings.Pitch;
        OutputGainSlider.Value = _settings.OutputGainDb;
        NoiseGateToggle.IsChecked = _settings.NoiseGateEnabled;
        GameGuardToggle.IsChecked = _settings.GameGuardEnabled;
        MonitorToggle.IsEnabled = false;
        MonitorToggle.ToolTip = "选择虚拟声卡为主输出后，可独立监听到物理耳机";
        UpdateLiveControlLabels();
        _deviceRefreshTimer.Tick += DeviceRefreshTimer_Tick;

        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            TrySaveSettings();
            _deviceRefreshTimer.Stop();
            StopAudioProcess();
            StopMonitorProcess();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        await RefreshAllAsync();
        _deviceRefreshTimer.Start();
    }

    private async void DeviceRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_refreshingDevices || _audioProcess is { HasExited: false } || DeviceOverlay.Visibility == Visibility.Visible) return;
        _refreshingDevices = true;
        try { await RefreshDevicesAsync(); }
        catch (Exception error) { FooterStatus.Text = $"设备热插拔检测失败：{FriendlyError(error)}"; }
        finally { _refreshingDevices = false; }
    }

    private async Task RefreshAllAsync()
    {
        FooterStatus.Text = "正在检测本机组件、音频设备与模型库…";
        try
        {
            _supervisorPath ??= FindNativeExecutable("FOXVOICE_SUPERVISOR", "foxvoice-supervisor.exe");
            _enginePath ??= FindNativeExecutable("FOXVOICE_ENGINE", "foxvoice-engine.exe");
            _converterPath ??= FindNativeExecutable("FOXVOICE_CONVERTER", "foxvoice-converter.exe");
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
        try { await RefreshFoundationModelsAsync(applyPaths: true); }
        catch (Exception error) { failures.Add($"基础模型：{FriendlyError(error)}"); }

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
        _virtualCableInput = inputs.FirstOrDefault(IsVirtualCableInput);

        _suppressDeviceSelection = true;
        InputDeviceCombo.ItemsSource = inputs;
        OutputDeviceCombo.ItemsSource = outputs;
        InputDeviceCombo.SelectedItem = inputs.FirstOrDefault(device => device.Name == _settings.InputDevice)
            ?? inputs.FirstOrDefault(device => device.IsDefault)
            ?? inputs.FirstOrDefault();
        OutputDeviceCombo.SelectedItem = outputs.FirstOrDefault(device => device.Name == _settings.OutputDevice)
            ?? outputs.FirstOrDefault(device => device.IsDefault)
            ?? outputs.FirstOrDefault();
        var physicalOutputs = outputs.Where(device => !IsVirtualDevice(device)).ToList();
        _monitorDeviceCombo.ItemsSource = physicalOutputs;
        _monitorDeviceCombo.SelectedItem = physicalOutputs.FirstOrDefault(device => device.Name == _settings.MonitorOutputDevice)
            ?? physicalOutputs.FirstOrDefault(device => device.IsDefault)
            ?? physicalOutputs.FirstOrDefault();
        _suppressDeviceSelection = false;
        UpdateDeviceRouteText();

        var virtualCable = outputs.FirstOrDefault(device =>
            device.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        VirtualCableStateText.Text = virtualCable is null ? "未检测到" : "已检测到";
        VirtualCableStateText.Foreground = virtualCable is null
            ? new SolidColorBrush(Color.FromRgb(249, 200, 106))
            : (Brush)FindResource("SuccessBrush");
        UpdateMonitorAvailability();
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

    private async Task<bool> RefreshFoundationModelsAsync(bool applyPaths)
    {
        using var document = JsonDocument.Parse(await RunSupervisorAsync("foundation-models", "status"));
        var entries = document.RootElement.EnumerateArray().ToList();
        var contentVec = entries.First(item => item.GetProperty("id").GetString() == "contentvec");
        var rmvpe = entries.First(item => item.GetProperty("id").GetString() == "rmvpe");
        var ready = contentVec.GetProperty("verified").GetBoolean()
            && rmvpe.GetProperty("verified").GetBoolean();
        _foundationStateText.Text = ready ? "已校验" : "未安装";
        _foundationStateText.Foreground = ready
            ? (Brush)FindResource("SuccessBrush")
            : new SolidColorBrush(Color.FromRgb(249, 200, 106));
        _installFoundationButton.Content = ready ? "重新校验" : "安装基础模型";
        if (ready && applyPaths)
        {
            EmbedderPath.Text = contentVec.GetProperty("path").GetString() ?? "";
            F0Path.Text = rmvpe.GetProperty("path").GetString() ?? "";
            TrySaveSettings();
        }
        return ready;
    }

    private async void InstallFoundationModels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await RefreshFoundationModelsAsync(applyPaths: true))
            {
                FooterStatus.Text = "ContentVec 与 RMVPE 文件和 SHA-256 均已校验";
                return;
            }
            var answer = MessageBox.Show(this,
                "将从 Hugging Face 的 wok000/weights_gpl 仓库下载 ContentVec 与 RMVPE，共约 741 MB。\n\n" +
                "这些权重采用 GPL-3.0，不属于 FoxVoice 的 MIT 代码，也不会打包进 FoxVoice。继续表示你接受上游许可。",
                "安装 RVC 基础模型", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (answer != MessageBoxResult.OK) return;
            _installFoundationButton.IsEnabled = false;
            FooterStatus.Text = "正在下载并校验基础模型（约 741 MB），请勿关闭程序…";
            await RunSupervisorAsync("foundation-models", "install", "--accept-gpl");
            await RefreshFoundationModelsAsync(applyPaths: true);
            FooterStatus.Text = "RVC 基础模型安装完成，来源、大小与 SHA-256 已校验";
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
        finally { _installFoundationButton.IsEnabled = true; }
    }

    private void AddFoundationModelsCard()
    {
        _installFoundationButton.Click += InstallFoundationModels_Click;
        var copy = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        copy.Children.Add(new TextBlock { Text = "RVC 基础模型", FontWeight = FontWeights.SemiBold, FontSize = 16 });
        copy.Children.Add(new TextBlock
        {
            Text = "ContentVec + RMVPE · 上游 GPL-3.0 · 按需下载并校验",
            Foreground = (Brush)FindResource("TextSecondary")
        });
        var action = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        action.Children.Add(_foundationStateText);
        action.Children.Add(_installFoundationButton);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var icon = new TextBlock
        {
            Text = "\uE8F1", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 23,
            Foreground = (Brush)FindResource("AccentPurple"), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(copy, 1);
        Grid.SetColumn(action, 2);
        grid.Children.Add(icon);
        grid.Children.Add(copy);
        grid.Children.Add(action);
        var card = new Border
        {
            Style = (Style)FindResource("Panel"), Margin = new Thickness(0, 0, 0, 10), Child = grid
        };
        if (ComponentsView.Content is StackPanel root && root.Children.OfType<StackPanel>().LastOrDefault() is StackPanel list)
            list.Children.Insert(1, card);
    }

    private void AddMonitorDevicePicker()
    {
        _monitorDeviceCombo.SelectionChanged += (_, _) =>
        {
            if (!_ready) return;
            UpdateMonitorAvailability();
            TrySaveSettings();
        };
        if (DeviceOverlay.Children.OfType<Border>().FirstOrDefault()?.Child is not Grid card) return;
        var routePanel = card.Children.OfType<StackPanel>().FirstOrDefault(child => Grid.GetRow(child) == 1);
        if (routePanel is null) return;
        routePanel.Children.Add(new TextBlock
        {
            Text = "本地监听设备（可选）",
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 14, 0, 0)
        });
        routePanel.Children.Add(_monitorDeviceCombo);
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

    private async void UseModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModelItem model }) return;
        if (!model.IsUsable)
        {
            if (model.Format == "pytorchCheckpoint")
            {
                try
                {
                    FooterStatus.Text = "正在隔离转换 RVC v2 F0 检查点，请稍候…";
                    var output = await RunSupervisorAsync("models", "convert", model.Id);
                    var marker = "FOXVOICE_RESULT_JSON=";
                    var markerIndex = output.LastIndexOf(marker, StringComparison.Ordinal);
                    if (markerIndex < 0) throw new InvalidOperationException("模型转换器没有返回结果");
                    using var converted = JsonDocument.Parse(output[(markerIndex + marker.Length)..].Trim());
                    var convertedId = converted.RootElement.GetProperty("id").GetString();
                    await RefreshModelsAsync();
                    SelectModel(_models.FirstOrDefault(item => item.Id == convertedId), persist: true);
                    StudioNav.IsChecked = true;
                    FooterStatus.Text = "转换完成，流式 ONNX 模型已通过结构校验并选中";
                }
                catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
            }
            else FooterStatus.Text = ".index 已保存，但游戏实时模式不会执行检索";
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
        if (url.Contains("/resolve/", StringComparison.OrdinalIgnoreCase))
        {
            await ImportModelAsync(["models", "huggingface", url], "正在从 Hugging Face 下载并校验…");
            return;
        }
        try
        {
            FooterStatus.Text = "正在读取 Hugging Face 仓库文件…";
            using var document = JsonDocument.Parse(await RunSupervisorAsync("models", "huggingface-files", url));
            var files = document.RootElement.EnumerateArray().Select(HuggingFaceFileItem.FromJson).ToList();
            if (files.Count == 0)
            {
                FooterStatus.Text = "此仓库没有 .onnx、.pth 或 .index 文件";
                return;
            }
            var selected = files.Count == 1 ? files[0] : ChooseHuggingFaceFile(files);
            if (selected is null) { FooterStatus.Text = "已取消仓库导入"; return; }
            HuggingFaceUrl.Text = selected.DownloadUrl;
            await ImportModelAsync(["models", "huggingface", selected.DownloadUrl], $"正在下载 {selected.Path} 并校验…");
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
    }

    private HuggingFaceFileItem? ChooseHuggingFaceFile(IReadOnlyList<HuggingFaceFileItem> files)
    {
        HuggingFaceFileItem? selected = null;
        var list = new ListBox { ItemsSource = files, DisplayMemberPath = nameof(HuggingFaceFileItem.DisplayLabel), Margin = new Thickness(0, 14, 0, 14) };
        list.SelectedIndex = 0;
        var dialog = new Window
        {
            Owner = this, Title = "选择 Hugging Face 模型文件", Width = 680, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("AppBackground"),
            Foreground = (Brush)FindResource("TextPrimary"), ResizeMode = ResizeMode.CanResizeWithGrip
        };
        var root = new DockPanel { Margin = new Thickness(22) };
        var title = new TextBlock { Text = "仓库中可导入的模型文件", FontSize = 20, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "下载并导入", Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) => { selected = list.SelectedItem as HuggingFaceFileItem; dialog.DialogResult = selected is not null; };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(list);
        dialog.Content = root;
        dialog.ShowDialog();
        return selected;
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
        ResetGameGuardState();
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
            if (MonitorToggle.IsChecked == true && _monitorProcess is not { HasExited: false })
                StartMonitorProcess();
        }
        if (snapshot.OutputSampleRate > 0) SampleRateText.Text = $"{snapshot.OutputSampleRate / 1000.0:0.#} kHz";
        UnderrunText.Text = snapshot.OutputUnderruns.ToString("N0");
        StreamErrorText.Text = snapshot.StreamErrors.ToString("N0");
        UpdateGameGuard(snapshot);
        if (snapshot.ProcessingUs > 0)
        {
            var milliseconds = snapshot.ProcessingUs / 1000.0;
            var budget = snapshot.ChunkMs > 0 ? snapshot.ChunkMs : DefaultProcessingBudgetMs;
            LatencyText.Text = $"{milliseconds:0.0} ms";
            BudgetText.Text = $"{milliseconds:0.0} / {budget:0} ms";
            BudgetProgress.Value = Math.Clamp(milliseconds / budget * 100.0, 0, 100);
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
        StopMonitorProcess();
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
            BudgetText.Text = $"-- / {DefaultProcessingBudgetMs:0} ms";
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
        if (!enabled)
        {
            ResetGameGuardState();
            _ = SendGuardProfileAsync("normal");
        }
        TrySaveSettings();
    }

    private void ResetGameGuardState()
    {
        _guardProfile = "normal";
        _guardOverloadStreak = 0;
        _guardRecoveryStreak = 0;
        _guardLastUnderruns = 0;
        _guardLastStreamErrors = 0;
        _guardCommandPending = false;
        if (IsInitialized) GuardStateText.Text = GameGuardToggle.IsChecked == true ? "资源保护已启用" : "资源保护已关闭";
    }

    private void UpdateGameGuard(EngineSnapshot snapshot)
    {
        if (GameGuardToggle.IsChecked != true || snapshot.State != "Running" || _guardCommandPending) return;
        var newFault = snapshot.OutputUnderruns > _guardLastUnderruns || snapshot.StreamErrors > _guardLastStreamErrors;
        _guardLastUnderruns = snapshot.OutputUnderruns;
        _guardLastStreamErrors = snapshot.StreamErrors;
        var budgetUs = (ulong)Math.Max(snapshot.ChunkMs, 1) * 1000;
        var overloaded = !snapshot.Passthrough && (snapshot.ProcessingUs >= budgetUs * 85 / 100 || newFault);
        var recovered = snapshot.Passthrough ? !newFault : snapshot.ProcessingUs > 0 && snapshot.ProcessingUs <= budgetUs / 2 && !newFault;
        if (overloaded)
        {
            _guardOverloadStreak++;
            _guardRecoveryStreak = 0;
        }
        else if (recovered)
        {
            _guardRecoveryStreak++;
            _guardOverloadStreak = 0;
        }
        else
        {
            _guardOverloadStreak = 0;
            _guardRecoveryStreak = 0;
        }

        var next = _guardProfile;
        if (_guardOverloadStreak >= (_guardProfile == "survival" ? 5 : 3))
            next = _guardProfile switch { "normal" => "stable", "stable" => "survival", "survival" => "bypass", _ => _guardProfile };
        else if (_guardRecoveryStreak >= (_guardProfile == "bypass" ? 20 : 30))
            next = _guardProfile switch { "bypass" => "survival", "survival" => "stable", "stable" => "normal", _ => _guardProfile };
        if (next == _guardProfile) return;
        _guardOverloadStreak = 0;
        _guardRecoveryStreak = 0;
        _ = SendGuardProfileAsync(next);
    }

    private async Task SendGuardProfileAsync(string level)
    {
        if (_audioProcess is not { HasExited: false } || _engineInput is null) return;
        _guardCommandPending = true;
        var command = JsonSerializer.Serialize(new { type = "guard", level });
        await _engineInputLock.WaitAsync();
        try
        {
            await _engineInput.WriteLineAsync(command);
            await _engineInput.FlushAsync();
            _guardProfile = level;
            GuardStateText.Text = level switch
            {
                "stable" => "稳定档 · 240 ms",
                "survival" => "保生存档 · 320 ms",
                "bypass" => "保护旁路 · 等待恢复",
                _ => "资源保护已启用"
            };
            FooterStatus.Text = level switch
            {
                "stable" => "检测到连续超载：已切换稳定档，主链路重新装载中",
                "survival" => "负载仍高：已扩大实时缓冲并减少上下文",
                "bypass" => "持续无法满足预算：已进入保护旁路，避免游戏语音中断",
                _ => "负载持续稳定：已恢复标准实时配置"
            };
        }
        catch (IOException) { }
        finally
        {
            _engineInputLock.Release();
            _guardCommandPending = false;
        }
    }

    private void Monitor_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (MonitorToggle.IsChecked == true) StartMonitorProcess();
        else StopMonitorProcess();
    }

    private void StartMonitorProcess()
    {
        if (_monitorProcess is { HasExited: false }) return;
        if (_audioProcess is not { HasExited: false })
        {
            FooterStatus.Text = "先启动实时变声，再开启本地监听";
            return;
        }
        if (_enginePath is null || _virtualCableInput is null || _monitorDeviceCombo.SelectedItem is not AudioDevice output)
        {
            MonitorToggle.IsChecked = false;
            FooterStatus.Text = "双输出需要虚拟声卡输入端和一个物理监听设备";
            return;
        }
        var info = CreateStartInfo(_enginePath,
            ["passthrough", "--monitor", "--input", _virtualCableInput.Name, "--output", output.Name], redirectInput: false);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_monitorProcess, process)) return;
            _monitorProcess = null;
            process.Dispose();
            if (MonitorToggle.IsChecked == true)
            {
                MonitorToggle.IsChecked = false;
                FooterStatus.Text = "本地监听已退出；送往游戏的主变声链路不受影响";
            }
        });
        try
        {
            process.Start();
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            _monitorProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            FooterStatus.Text = $"双输出已启用：游戏走虚拟声卡，监听走 {output.Name}";
        }
        catch (Exception error)
        {
            process.Dispose();
            MonitorToggle.IsChecked = false;
            FooterStatus.Text = $"无法启动本地监听：{FriendlyError(error)}";
        }
    }

    private void StopMonitorProcess()
    {
        var process = _monitorProcess;
        _monitorProcess = null;
        if (process is { HasExited: false })
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }
        process?.Dispose();
    }

    private void UpdateMonitorAvailability()
    {
        var primaryOutput = OutputDeviceCombo.SelectedItem as AudioDevice;
        var ready = _virtualCableInput is not null
            && primaryOutput is not null && IsVirtualDevice(primaryOutput)
            && _monitorDeviceCombo.SelectedItem is AudioDevice;
        MonitorToggle.IsEnabled = ready;
        MonitorToggle.ToolTip = ready
            ? "使用独立低优先级监听进程，不阻塞送往游戏的主链路"
            : "请把主输出选择为 VB-CABLE，并选择一个物理监听设备";
        if (!ready && MonitorToggle.IsChecked == true) MonitorToggle.IsChecked = false;
    }

    private static bool IsVirtualDevice(AudioDevice device) =>
        device.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        || device.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    private static bool IsVirtualCableInput(AudioDevice device) =>
        device.Direction == "input" && IsVirtualDevice(device);

    private void AddVirtualCableInstallAction()
    {
        if (VirtualCableStateText.Parent is not Grid grid) return;
        grid.Children.Remove(VirtualCableStateText);
        var button = new Button { Content = "官方安装说明", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 8, 0, 0) };
        button.Click += (_, _) => Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        var action = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        action.Children.Add(VirtualCableStateText);
        action.Children.Add(button);
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
    }

    private void AddRvcSelfTestAction()
    {
        _rvcSelfTestButton.Click += RvcSelfTest_Click;
        if (EmbedderPath.Parent is Grid { Parent: StackPanel panel }) panel.Children.Add(_rvcSelfTestButton);
    }

    private async void RvcSelfTest_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedModel is null)
        {
            ModelsNav.IsChecked = true;
            FooterStatus.Text = "请先选择一个可用的 ONNX RVC Generator";
            return;
        }
        if (!File.Exists(EmbedderPath.Text) || !File.Exists(F0Path.Text))
        {
            FooterStatus.Text = "请先安装或选择 ContentVec 与 RMVPE";
            return;
        }
        try
        {
            _rvcSelfTestButton.IsEnabled = false;
            FooterStatus.Text = "正在用 WindowsML/DirectML 加载三模型并执行一帧推理…";
            using var resolved = JsonDocument.Parse(await RunSupervisorAsync("models", "resolve", _selectedModel.Id));
            var modelPath = resolved.RootElement.GetProperty("path").GetString()
                ?? throw new InvalidOperationException("模型路径解析失败");
            var raw = await RunEngineCommandAsync("validate-rvc", "--model", modelPath,
                "--embedder", EmbedderPath.Text, "--f0", F0Path.Text);
            using var report = JsonDocument.Parse(raw);
            var root = report.RootElement;
            var loadMs = root.GetProperty("loadMs").GetDouble();
            var inferenceMs = root.GetProperty("inferenceMs").GetDouble();
            var outputSamples = root.GetProperty("outputSamples").GetInt32();
            DoctorText.Text = $"RVC 三模型自检通过\n\n后端：WindowsML / DirectML\n模型加载：{loadMs:N0} ms\n单帧推理：{inferenceMs:N1} ms\n输出采样：{outputSamples:N0}\n\n" + DoctorText.Text;
            FooterStatus.Text = $"RVC 自检通过：加载 {loadMs:N0} ms，单帧推理 {inferenceMs:N1} ms";
        }
        catch (Exception error)
        {
            FooterStatus.Text = $"RVC 自检失败：{FriendlyError(error)}";
        }
        finally { _rvcSelfTestButton.IsEnabled = true; }
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
        UpdateMonitorAvailability();
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
            _settings.MonitorOutputDevice = (_monitorDeviceCombo.SelectedItem as AudioDevice)?.Name ?? "";
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

    private async Task<string> RunEngineCommandAsync(params string[] arguments)
    {
        if (_enginePath is null) throw new InvalidOperationException("原生推理引擎尚未连接");
        using var process = new Process { StartInfo = CreateStartInfo(_enginePath, arguments, redirectInput: false) };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "推理自检失败" : error.Trim());
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

    private sealed record HuggingFaceFileItem(string Path, long SizeBytes, string DownloadUrl)
    {
        public string DisplayLabel => $"{Path}    ({SizeBytes / 1024d / 1024d:N1} MB)";
        public static HuggingFaceFileItem FromJson(JsonElement value) => new(
            value.GetProperty("path").GetString() ?? "",
            value.GetProperty("sizeBytes").GetInt64(),
            value.GetProperty("downloadUrl").GetString() ?? "");
    }

    private sealed record EngineSnapshot(
        string? State,
        string? Message,
        bool Passthrough,
        int OutputSampleRate,
        ulong ProcessingUs,
        int ChunkMs,
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
            var chunkMs = root.TryGetProperty("chunkMs", out var chunkValue) ? chunkValue.GetInt32() : (int)DefaultProcessingBudgetMs;
            var underruns = root.TryGetProperty("outputUnderruns", out var underrunValue) ? underrunValue.GetUInt64() : 0;
            var streamErrors = root.TryGetProperty("streamErrors", out var errorValue) ? errorValue.GetUInt64() : 0;
            var passthrough = eventName == "audioStarted"
                || (root.TryGetProperty("passthrough", out var passthroughValue) && passthroughValue.GetBoolean())
                || (root.TryGetProperty("mode", out var modeValue) && modeValue.GetString() == "safeBypass");
            return new(state, message, passthrough, sampleRate, processing, chunkMs, underruns, streamErrors);
        }
    }
}
