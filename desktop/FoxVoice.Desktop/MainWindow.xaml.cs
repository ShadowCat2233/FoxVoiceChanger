using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FoxVoice.Desktop;

public partial class MainWindow : Window
{
    private const double DefaultProcessingBudgetMs = 160.0;
    private readonly UserSettings _settings;
    private readonly SemaphoreSlim _engineInputLock = new(1, 1);
    private readonly SemaphoreSlim _modelActivityLock = new(1, 1);
    private readonly DispatcherTimer _deviceRefreshTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _gameDetectionTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Process? _audioProcess;
    private Process? _monitorProcess;
    private Process? _trainingProcess;
    private Process? _modelTransferProcess;
    private readonly HashSet<Process> _soundProcesses = [];
    private readonly Dictionary<string, Process> _loopingSoundProcesses = new(StringComparer.OrdinalIgnoreCase);
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
    private bool _gameDetected;
    private bool _trainingReady;
    private int _gameDetectionStreak;
    private int _gameReleaseStreak;
    private string _detectedGameName = "";
    private IntPtr _windowHandle;
    private HwndSource? _windowSource;
    private AudioDevice? _virtualCableInput;
    private readonly ComboBox _monitorDeviceCombo = new() { DisplayMemberPath = "Name", Margin = new Thickness(0, 6, 0, 12) };
    private readonly TextBlock _foundationStateText = new() { Text = "检测中", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _installFoundationButton = new() { Content = "安装基础模型", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _rvcSelfTestButton = new() { Content = "运行 RVC 三模型自检", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _tensorRtStateText = new() { Text = "检测中", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _installTensorRtButton = new() { Content = "安装并自检", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _cancelModelTransferButton = new() { Content = "取消下载", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly WrapPanel _soundboardPanel = new();
    private readonly ComboBox _soundboardGroupFilter = new() { Width = 120, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Slider _soundboardGain = new() { Minimum = -36, Maximum = 12, Width = 180, TickFrequency = 3, IsSnapToTickEnabled = false };
    private readonly TextBlock _trainingStateText = new() { Text = "检测中", TextWrapping = TextWrapping.Wrap };
    private readonly Button _installCudaTrainingButton = new() { Content = "安装 NVIDIA CUDA 训练环境", Padding = new Thickness(14, 8, 14, 8) };
    private readonly Button _installCpuTrainingButton = new() { Content = "安装 CPU 训练环境", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _cancelTrainingButton = new() { Content = "停止当前任务", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly Button _launchTrainingButton = new() { Content = "打开训练工作台", Padding = new Thickness(14, 8, 14, 8), IsEnabled = false };
    private readonly Button _importTrainingButton = new() { Content = "导入训练结果", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly TextBox _trainingDatasetText = new() { MinWidth = 360 };
    private readonly TextBox _trainingNameText = new() { Text = "foxvoice", Width = 150 };
    private readonly TextBox _trainingEpochsText = new() { Text = "20", Width = 70 };
    private readonly TextBox _trainingBatchText = new() { Text = "4", Width = 70 };
    private readonly Button _browseTrainingDatasetButton = new() { Content = "选择数据集", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _startTrainingButton = new() { Content = "开始一键训练", Padding = new Thickness(14, 8, 14, 8), IsEnabled = false };
    private readonly TextBlock _trainingDatasetSummary = new() { Text = "请选择包含 WAV 或 FLAC 的文件夹", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _trainingProgressText = new() { Text = "等待任务", FontWeight = FontWeights.SemiBold };
    private readonly ProgressBar _trainingProgress = new() { Minimum = 0, Maximum = 100, Height = 7, Margin = new Thickness(0, 8, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(46, 226, 247)), Background = new SolidColorBrush(Color.FromRgb(38, 48, 64)) };
    private readonly TextBox _trainingValidationAudio = new() { IsReadOnly = true, MinWidth = 330 };
    private readonly ComboBox _trainingPreviewModelA = new() { Width = 230, DisplayMemberPath = "DisplayName", Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _trainingPreviewModelB = new() { Width = 230, DisplayMemberPath = "DisplayName", Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _generateTrainingPreviewsButton = new() { Content = "生成 A/B 试听", Padding = new Thickness(14, 8, 14, 8) };
    private readonly Button _playTrainingOriginalButton = new() { Content = "播放原声", Padding = new Thickness(12, 7, 12, 7), IsEnabled = false };
    private readonly Button _playTrainingAButton = new() { Content = "播放 A", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly Button _playTrainingBButton = new() { Content = "播放 B", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly TextBlock _trainingPreviewStatus = new() { Text = "选择固定测试音频与两个可用模型", TextWrapping = TextWrapping.Wrap };
    private readonly MediaPlayer _trainingPreviewPlayer = new();
    private string? _trainingPreviewAPath;
    private string? _trainingPreviewBPath;

    public MainWindow()
    {
        _settings = UserSettings.Load();
        InitializeComponent();
        VersionText.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"} local";
        AddFoundationModelsCard();
        AddTensorRtAction();
        AddMonitorDevicePicker();
        AddVirtualCableInstallAction();
        AddRvcSelfTestAction();
        AddModelRecycleAction();
        AddModelTransferCancelAction();
        BuildSoundboardView();
        BuildTrainingView();
        LanguageCombo.SelectedIndex = UiText.IsEnglish(_settings.Language) ? 1 : 0;
        EmbedderPath.Text = _settings.EmbedderPath;
        F0Path.Text = _settings.F0Path;
        PitchSlider.Value = _settings.Pitch;
        OutputGainSlider.Value = _settings.OutputGainDb;
        IndexRateSlider.Value = _settings.IndexRate;
        ProtectSlider.Value = _settings.Protect;
        F0SmoothingToggle.IsChecked = _settings.F0Smoothing;
        NoiseGateToggle.IsChecked = _settings.NoiseGateEnabled;
        GameGuardToggle.IsChecked = _settings.GameGuardEnabled;
        MonitorToggle.IsEnabled = false;
        MonitorToggle.ToolTip = "选择虚拟声卡为主输出后，可独立监听到物理耳机";
        UpdateLiveControlLabels();
        ApplyLocalization();
        _deviceRefreshTimer.Tick += DeviceRefreshTimer_Tick;
        _gameDetectionTimer.Tick += GameDetectionTimer_Tick;

        Loaded += MainWindow_Loaded;
        SourceInitialized += (_, _) => InitializeSoundboardHotkeys();
        Closing += (_, _) =>
        {
            TrySaveSettings();
            _deviceRefreshTimer.Stop();
            _gameDetectionTimer.Stop();
            StopAudioProcess();
            StopMonitorProcess();
            StopSoundboardProcesses();
            StopTrainingProcess();
            StopModelTransfer();
            ReleaseSoundboardHotkeys();
            _trainingPreviewPlayer.Close();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        await RefreshAllAsync();
        ApplyLocalization();
        _deviceRefreshTimer.Start();
        _gameDetectionTimer.Start();
    }

    private void GameDetectionTimer_Tick(object? sender, EventArgs e)
    {
        if (UiText.IsEnglish(_settings.Language)) ApplyLocalization();
        var candidate = GameGuardToggle.IsChecked == true ? DetectForegroundGame() : null;
        if (candidate is not null)
        {
            _gameReleaseStreak = 0;
            _gameDetectionStreak++;
            if (_gameDetectionStreak < 2) return;
            var changed = !_gameDetected || !string.Equals(_detectedGameName, candidate, StringComparison.OrdinalIgnoreCase);
            _gameDetected = true;
            _detectedGameName = candidate;
            if (changed)
            {
                GuardStateText.Text = $"游戏保护：已检测到 {candidate}";
                if (_audioProcess is { HasExited: false } && _guardProfile == "normal")
                    _ = SendGuardProfileAsync("stable");
                if (_trainingProcess is { HasExited: false })
                {
                    StopTrainingProcess();
                    FooterStatus.Text = $"检测到 {candidate}，安装或训练任务已停止；下载缓存和训练检查点会保留";
                }
                if (_modelTransferProcess is { HasExited: false })
                {
                    StopModelTransfer();
                    FooterStatus.Text = $"检测到 {candidate}，模型下载已暂停；退出游戏后重试将从断点继续";
                }
            }
            return;
        }

        _gameDetectionStreak = 0;
        if (!_gameDetected || ++_gameReleaseStreak < 3) return;
        _gameDetected = false;
        _gameReleaseStreak = 0;
        _detectedGameName = "";
        if (_audioProcess is not { HasExited: false })
            GuardStateText.Text = "资源保护已启用，等待游戏或实时引擎";
    }

    private string? DetectForegroundGame()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _windowHandle || IsIconic(foreground)) return null;
        if (!GetWindowRect(foreground, out var windowRect)) return null;
        var monitor = MonitorFromWindow(foreground, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        var monitorWidth = Math.Max(1, info.Monitor.Right - info.Monitor.Left);
        var monitorHeight = Math.Max(1, info.Monitor.Bottom - info.Monitor.Top);
        var widthRatio = (double)(windowRect.Right - windowRect.Left) / monitorWidth;
        var heightRatio = (double)(windowRect.Bottom - windowRect.Top) / monitorHeight;
        if (widthRatio < 0.90 || heightRatio < 0.90) return null;
        _ = GetWindowThreadProcessId(foreground, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            return IgnoredFullscreenProcesses.Contains(name) ? null : name;
        }
        catch { return null; }
    }

    private bool RejectHeavyWorkDuringGame(string operation)
    {
        if (!_gameDetected) return false;
        FooterStatus.Text = $"已暂停{operation}：{_detectedGameName} 正在全屏或无边框运行；退出游戏画面后重试";
        return true;
    }

    private static readonly HashSet<string> IgnoredFullscreenProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "SearchHost", "StartMenuExperienceHost", "LockApp", "ApplicationFrameHost",
        "chrome", "msedge", "firefox", "FoxVoice.Desktop"
    };

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
        try { await RefreshTrainingStatusAsync(); }
        catch (Exception error) { failures.Add($"训练组件：{FriendlyError(error)}"); }
        try { await RefreshTensorRtStatusAsync(); }
        catch (Exception error) { failures.Add($"TensorRT RTX：{FriendlyError(error)}"); }

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
        var missingProfiles = _models.Where(model => model.IsUsable && model.RvcVersion is null).ToList();
        if (missingProfiles.Count > 0)
        {
            foreach (var model in missingProfiles)
            {
                try { await RunSupervisorAsync("models", "rescan", model.Id); }
                catch { /* 旧模型的可选资料扫描失败不应阻止模型库打开。 */ }
            }
            using var rescanned = JsonDocument.Parse(await RunSupervisorAsync("models", "list"));
            _models = rescanned.RootElement.EnumerateArray().Select(ModelItem.FromJson).ToList();
        }
        ModelsList.ItemsSource = _models;
        var usable = _models.Where(model => model.IsUsable).ToList();
        PresetItems.ItemsSource = usable.Take(4).ToList();
        EmptyPresetButton.Visibility = usable.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyModelsState.Visibility = _models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var selected = _models.FirstOrDefault(model => model.Id == _settings.SelectedModelId && model.IsUsable)
            ?? usable.FirstOrDefault();
        SelectModel(selected, persist: false);
        RefreshTrainingPreviewModels(usable);
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
        if (RejectHeavyWorkDuringGame("基础模型下载")) return;
        try
        {
            if (await RefreshFoundationModelsAsync(applyPaths: true))
            {
                FooterStatus.Text = "ContentVec 与 RMVPE 文件和 SHA-256 均已校验";
                return;
            }
            var answer = MessageBox.Show(this,
                UiText.IsEnglish(UiText.CurrentLanguage)
                    ? "Download ContentVec and RMVPE (about 741 MB) from the wok000/weights_gpl repository on Hugging Face.\n\nThese weights use GPL-3.0, are not part of FoxVoice's MIT code, and are not bundled. Continuing means you accept the upstream license."
                    : "将从 Hugging Face 的 wok000/weights_gpl 仓库下载 ContentVec 与 RMVPE，共约 741 MB。\n\n这些权重采用 GPL-3.0，不属于 FoxVoice 的 MIT 代码，也不会打包进 FoxVoice。继续表示你接受上游许可。",
                T("安装 RVC 基础模型"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
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

    private void AddTensorRtAction()
    {
        _installTensorRtButton.Click += InstallTensorRt_Click;
        var title = FindDescendant<TextBlock>(ComponentsView, value => Equals(value.Text, "TensorRT 高性能引擎"));
        if (title?.Parent is not StackPanel || VisualTreeHelper.GetParent(title.Parent) is not Grid row) return;
        var oldState = row.Children.OfType<TextBlock>().FirstOrDefault(value => Grid.GetColumn(value) == 2);
        if (oldState is not null) row.Children.Remove(oldState);
        var actions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_tensorRtStateText);
        actions.Children.Add(_installTensorRtButton);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
    }

    private async Task RefreshTensorRtStatusAsync()
    {
        using var document = JsonDocument.Parse(await RunEngineCommandAsync("provider-status"));
        var provider = document.RootElement.EnumerateArray().FirstOrDefault(value =>
            value.GetProperty("name").GetString()?.Contains("NvTensorRT", StringComparison.OrdinalIgnoreCase) == true);
        var state = provider.ValueKind == JsonValueKind.Undefined
            ? "不受支持"
            : provider.GetProperty("readyState").GetString() ?? "未知";
        var ready = state.Equals("Ready", StringComparison.OrdinalIgnoreCase);
        _tensorRtStateText.Text = ready ? "已安装" : state == "NotPresent" ? "可按需安装" : state == "NotReady" ? "需要准备" : state;
        _tensorRtStateText.Foreground = ready ? (Brush)FindResource("SuccessBrush") : new SolidColorBrush(Color.FromRgb(249, 200, 106));
        _installTensorRtButton.Content = ready ? "重新自检" : "安装并自检";
        if (ready) _settings.PreferredProvider = "nvtrtx";
    }

    private async void InstallTensorRt_Click(object sender, RoutedEventArgs e)
    {
        if (RejectHeavyWorkDuringGame("TensorRT RTX 安装与自检")) return;
        if (_selectedModel is null || !File.Exists(EmbedderPath.Text) || !File.Exists(F0Path.Text))
        {
            FooterStatus.Text = "TensorRT RTX 真实自检需要先选择 Generator，并安装 ContentVec 与 RMVPE";
            return;
        }
        var answer = MessageBox.Show(this,
            UiText.IsEnglish(UiText.CurrentLanguage)
                ? "Windows ML will acquire the NVIDIA TensorRT RTX execution provider on demand. It follows NVIDIA's license and is not bundled with FoxVoice.\n\nFoxVoice will run real inference with the current three models and select it only after a successful test."
                : "Windows ML 将按需获取 NVIDIA TensorRT RTX 执行提供程序。该组件遵循 NVIDIA 软件许可，不随 FoxVoice 打包。\n\n继续后会用当前三模型执行一帧真实推理；只有成功才会设为首选后端。",
            T("安装 TensorRT RTX"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            _installTensorRtButton.IsEnabled = false;
            FooterStatus.Text = "正在通过 Windows ML 准备 TensorRT RTX 并执行三模型自检…";
            using var resolved = JsonDocument.Parse(await RunSupervisorAsync("models", "resolve", _selectedModel.Id));
            var modelPath = resolved.RootElement.GetProperty("path").GetString()
                ?? throw new InvalidOperationException("模型路径解析失败");
            using var report = JsonDocument.Parse(await RunEngineCommandAsync(
                "validate-rvc", "--provider", "nvtrtx", "--frames", "20", "--model", modelPath,
                "--embedder", EmbedderPath.Text, "--f0", F0Path.Text));
            await RecordModelTestAsync(_selectedModel.Id, true, "nvtrtx");
            _settings.PreferredProvider = "nvtrtx";
            TrySaveSettings();
            await RefreshTensorRtStatusAsync();
            FooterStatus.Text = $"TensorRT RTX 三模型自检通过，单帧 {report.RootElement.GetProperty("inferenceMs").GetDouble():N1} ms";
        }
        catch (Exception error)
        {
            if (_selectedModel is not null) await RecordModelTestAsync(_selectedModel.Id, false, "nvtrtx");
            _settings.PreferredProvider = "directml";
            FooterStatus.Text = $"TensorRT RTX 未启用：{FriendlyError(error)}";
        }
        finally { _installTensorRtButton.IsEnabled = true; }
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

    private void BuildTrainingView()
    {
        _installCudaTrainingButton.Click += async (_, _) => await InstallTrainingAsync("cuda");
        _installCpuTrainingButton.Click += async (_, _) => await InstallTrainingAsync("cpu");
        _cancelTrainingButton.Click += (_, _) => StopTrainingProcess();
        _launchTrainingButton.Click += async (_, _) => await LaunchTrainingWorkbenchAsync();
        _importTrainingButton.Click += async (_, _) => await ImportTrainingOutputsAsync();
        _browseTrainingDatasetButton.Click += (_, _) => BrowseTrainingDataset();
        _startTrainingButton.Click += async (_, _) => await RunNativeTrainingAsync();
        _generateTrainingPreviewsButton.Click += async (_, _) => await GenerateTrainingPreviewsAsync();
        _playTrainingOriginalButton.Click += (_, _) => PlayTrainingPreview(_settings.TrainingValidationAudioPath, false);
        _playTrainingAButton.Click += (_, _) => PlayTrainingPreview(_trainingPreviewAPath, true);
        _playTrainingBButton.Click += (_, _) => PlayTrainingPreview(_trainingPreviewBPath, true);
        _trainingValidationAudio.Text = _settings.TrainingValidationAudioPath;
        TrainingView.Children.Clear();
        var root = new StackPanel { Margin = new Thickness(28, 26, 28, 26) };
        root.Children.Add(new TextBlock { Text = "MODEL TRAINING", Style = (Style)FindResource("Eyebrow") });
        root.Children.Add(new TextBlock { Text = "模型训练", Style = (Style)FindResource("SectionTitle") });
        var card = new Border { Style = (Style)FindResource("Panel"), Margin = new Thickness(0, 14, 0, 14), Padding = new Thickness(20) };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "隔离训练组件", FontSize = 18, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = "固定官方 RVC 源代码版本；Python 3.12、FFmpeg、PyTorch 和训练权重按需安装到 LocalAppData，不进入实时引擎。",
            Foreground = (Brush)FindResource("TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 14)
        });
        content.Children.Add(_trainingStateText);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(_installCudaTrainingButton);
        actions.Children.Add(_installCpuTrainingButton);
        actions.Children.Add(_cancelTrainingButton);
        content.Children.Add(actions);
        content.Children.Add(new TextBlock { Text = "应用内一键训练", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 8) });
        content.Children.Add(new TextBlock { Text = "数据集目录（本人授权的干净人声 WAV/FLAC）", Foreground = (Brush)FindResource("TextSecondary") });
        var datasetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
        datasetRow.Children.Add(_trainingDatasetText);
        datasetRow.Children.Add(_browseTrainingDatasetButton);
        content.Children.Add(datasetRow);
        _trainingDatasetSummary.Foreground = (Brush)FindResource("TextSecondary");
        _trainingDatasetSummary.Margin = new Thickness(0, 0, 0, 12);
        content.Children.Add(_trainingDatasetSummary);
        var parameters = new WrapPanel();
        parameters.Children.Add(new TextBlock { Text = "名称", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        parameters.Children.Add(_trainingNameText);
        parameters.Children.Add(new TextBlock { Text = "轮数", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 7, 0) });
        parameters.Children.Add(_trainingEpochsText);
        parameters.Children.Add(new TextBlock { Text = "批大小", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 7, 0) });
        parameters.Children.Add(_trainingBatchText);
        content.Children.Add(parameters);
        var nativeActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        nativeActions.Children.Add(_startTrainingButton);
        content.Children.Add(nativeActions);
        var workbenchActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        workbenchActions.Children.Add(_launchTrainingButton);
        workbenchActions.Children.Add(_importTrainingButton);
        content.Children.Add(workbenchActions);
        var progressCard = new Border { Background = new SolidColorBrush(Color.FromRgb(16, 27, 39)), BorderBrush = new SolidColorBrush(Color.FromRgb(35, 68, 63)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(14), Margin = new Thickness(0, 18, 0, 0) };
        var progressContent = new StackPanel();
        progressContent.Children.Add(_trainingProgressText);
        progressContent.Children.Add(_trainingProgress);
        progressContent.Children.Add(new TextBlock { Text = "数据清洗 → 特征提取 → 模型训练 → 生成索引", Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 7, 0, 0) });
        progressCard.Child = progressContent;
        content.Children.Add(progressCard);
        card.Child = content;
        var previewCard = new Border { Style = (Style)FindResource("Panel"), Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(20) };
        var previewContent = new StackPanel();
        previewContent.Children.Add(new TextBlock { Text = "固定测试音频 · A/B 试听", FontSize = 18, FontWeight = FontWeights.SemiBold });
        previewContent.Children.Add(new TextBlock { Text = "使用同一段音频和相同参数比较两个训练检查点；切换 A/B 时保持播放位置。", Foreground = (Brush)FindResource("TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14) });
        var validationRow = new WrapPanel();
        validationRow.Children.Add(_trainingValidationAudio);
        var chooseValidation = new Button { Content = "选择测试音频", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0) };
        chooseValidation.Click += ChooseTrainingValidationAudio_Click;
        validationRow.Children.Add(chooseValidation);
        previewContent.Children.Add(validationRow);
        var modelRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        modelRow.Children.Add(new TextBlock { Text = "A", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 7, 0) });
        modelRow.Children.Add(_trainingPreviewModelA);
        modelRow.Children.Add(new TextBlock { Text = "B", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 7, 0) });
        modelRow.Children.Add(_trainingPreviewModelB);
        modelRow.Children.Add(_generateTrainingPreviewsButton);
        previewContent.Children.Add(modelRow);
        var playRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        playRow.Children.Add(_playTrainingOriginalButton);
        playRow.Children.Add(_playTrainingAButton);
        playRow.Children.Add(_playTrainingBButton);
        previewContent.Children.Add(playRow);
        _trainingPreviewStatus.Foreground = (Brush)FindResource("TextSecondary");
        _trainingPreviewStatus.Margin = new Thickness(0, 10, 0, 0);
        previewContent.Children.Add(_trainingPreviewStatus);
        previewCard.Child = previewContent;
        root.Children.Add(previewCard);
        root.Children.Add(card);
        root.Children.Add(new TextBlock
        {
            Text = "安装可能下载数 GB 数据。检测到全屏/无边框游戏时会停止安装或训练进程；检查点会保留，可稍后继续。默认训练 RVC v2、40 kHz、F0/RMVPE，并生成检索索引。",
            Foreground = (Brush)FindResource("TextSecondary"), TextWrapping = TextWrapping.Wrap
        });
        TrainingView.Children.Add(new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
    }

    private async Task RefreshTrainingStatusAsync()
    {
        using var document = JsonDocument.Parse(await RunSupervisorAsync("training", "status"));
        var root = document.RootElement;
        var ready = root.GetProperty("ready").GetBoolean();
        var source = root.GetProperty("sourceReady").GetBoolean();
        var python = root.GetProperty("pythonReady").GetBoolean();
        var torch = root.GetProperty("torchReady").GetBoolean();
        var cuda = root.GetProperty("cudaReady").GetBoolean();
        var models = root.GetProperty("modelsReady").GetBoolean();
        _trainingReady = ready;
        _trainingStateText.Text = ready
            ? $"环境已就绪 · 源代码 ✓  Python ✓  PyTorch ✓  基础权重 ✓  CUDA {(cuda ? "✓" : "未启用")}"
            : $"尚未就绪 · 源代码 {(source ? "✓" : "—")}  Python {(python ? "✓" : "—")}  PyTorch {(torch ? "✓" : "—")}  基础权重 {(models ? "✓" : "—")}";
        _trainingStateText.Foreground = ready ? (Brush)FindResource("SuccessBrush") : new SolidColorBrush(Color.FromRgb(249, 200, 106));
        _launchTrainingButton.IsEnabled = ready && _trainingProcess is not { HasExited: false };
        _importTrainingButton.IsEnabled = ready && _trainingProcess is not { HasExited: false };
        _startTrainingButton.IsEnabled = ready && _trainingProcess is not { HasExited: false };
    }

    private void RefreshTrainingPreviewModels(IReadOnlyList<ModelItem> usable)
    {
        var selectedA = (_trainingPreviewModelA.SelectedItem as ModelItem)?.Id;
        var selectedB = (_trainingPreviewModelB.SelectedItem as ModelItem)?.Id;
        _trainingPreviewModelA.ItemsSource = usable;
        _trainingPreviewModelB.ItemsSource = usable;
        _trainingPreviewModelA.SelectedItem = usable.FirstOrDefault(model => model.Id == selectedA) ?? usable.FirstOrDefault();
        _trainingPreviewModelB.SelectedItem = usable.FirstOrDefault(model => model.Id == selectedB) ?? usable.Skip(1).FirstOrDefault() ?? usable.FirstOrDefault();
        _generateTrainingPreviewsButton.IsEnabled = usable.Count > 0 && File.Exists(_settings.TrainingValidationAudioPath);
        _playTrainingOriginalButton.IsEnabled = File.Exists(_settings.TrainingValidationAudioPath);
    }

    private void ChooseTrainingValidationAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择固定测试音频",
            Filter = "支持的音频 (*.wav;*.flac;*.mp3;*.ogg)|*.wav;*.flac;*.mp3;*.ogg"
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.TrainingValidationAudioPath = dialog.FileName;
        _trainingValidationAudio.Text = dialog.FileName;
        _trainingPreviewAPath = null;
        _trainingPreviewBPath = null;
        _playTrainingOriginalButton.IsEnabled = true;
        _playTrainingAButton.IsEnabled = false;
        _playTrainingBButton.IsEnabled = false;
        _generateTrainingPreviewsButton.IsEnabled = _trainingPreviewModelA.Items.Count > 0;
        _trainingPreviewStatus.Text = "测试音频已固定；请选择 A/B 模型并生成试听";
        TrySaveSettings();
    }

    private async Task GenerateTrainingPreviewsAsync()
    {
        if (RejectHeavyWorkDuringGame("训练检查点试听")) return;
        if (_audioProcess is { HasExited: false }) { _trainingPreviewStatus.Text = "请先停止实时变声"; return; }
        if (_trainingPreviewModelA.SelectedItem is not ModelItem modelA || _trainingPreviewModelB.SelectedItem is not ModelItem modelB)
        { _trainingPreviewStatus.Text = "请选择两个可用的 ONNX 模型"; return; }
        if (!File.Exists(_settings.TrainingValidationAudioPath) || !File.Exists(EmbedderPath.Text) || !File.Exists(F0Path.Text))
        { _trainingPreviewStatus.Text = "请先选择测试音频并安装 ContentVec 与 RMVPE"; return; }

        _generateTrainingPreviewsButton.IsEnabled = false;
        _playTrainingAButton.IsEnabled = false;
        _playTrainingBButton.IsEnabled = false;
        try
        {
            var previewRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxVoice", "training-previews");
            Directory.CreateDirectory(previewRoot);
            _trainingPreviewStatus.Text = $"正在生成 A：{modelA.DisplayName}";
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            _trainingPreviewAPath = await GenerateTrainingPreviewAsync(modelA, Path.Combine(previewRoot, $"A-{modelA.Hash[..Math.Min(12, modelA.Hash.Length)]}.wav"));
            _trainingPreviewStatus.Text = $"正在生成 B：{modelB.DisplayName}";
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            _trainingPreviewBPath = await GenerateTrainingPreviewAsync(modelB, Path.Combine(previewRoot, $"B-{modelB.Hash[..Math.Min(12, modelB.Hash.Length)]}.wav"));
            _playTrainingAButton.IsEnabled = true;
            _playTrainingBButton.IsEnabled = true;
            _trainingPreviewStatus.Text = $"试听已就绪：A {modelA.DisplayName} ↔ B {modelB.DisplayName}";
        }
        catch (Exception error) { _trainingPreviewStatus.Text = $"试听生成失败：{FriendlyError(error)}"; }
        finally { _generateTrainingPreviewsButton.IsEnabled = true; }
    }

    private async Task<string> GenerateTrainingPreviewAsync(ModelItem model, string outputPath)
    {
        using var resolved = JsonDocument.Parse(await RunSupervisorAsync("models", "resolve", model.Id));
        var modelPath = resolved.RootElement.GetProperty("path").GetString() ?? throw new InvalidOperationException("模型路径解析失败");
        var arguments = new List<string>
        {
            "convert-audio", "--input", _settings.TrainingValidationAudioPath, "--output", outputPath,
            "--model", modelPath, "--embedder", EmbedderPath.Text, "--f0", F0Path.Text,
            "--pitch", PitchSlider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--protect", ProtectSlider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (F0SmoothingToggle.IsChecked == true) arguments.Add("--f0-smoothing");
        await RunEngineCommandAsync(arguments.ToArray());
        return outputPath;
    }

    private void PlayTrainingPreview(string? path, bool preservePosition)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var position = preservePosition ? _trainingPreviewPlayer.Position : TimeSpan.Zero;
        _trainingPreviewPlayer.Close();
        _trainingPreviewPlayer.Open(new Uri(path, UriKind.Absolute));
        _trainingPreviewPlayer.Position = position;
        _trainingPreviewPlayer.Play();
    }

    private async Task InstallTrainingAsync(string backend)
    {
        if (RejectHeavyWorkDuringGame("训练组件安装")) return;
        if (_trainingProcess is { HasExited: false }) return;
        var label = backend == "cuda" ? "NVIDIA CUDA 12.8" : "CPU";
        var answer = MessageBox.Show(this,
            UiText.IsEnglish(UiText.CurrentLanguage)
                ? $"Install a pinned official RVC version and the {label} training environment. This may download several GB.\n\nRVC code is MIT; PyTorch, pretrained weights, and dependencies retain their own licenses. Continuing confirms acceptance and authorization for the training voices."
                : $"将安装官方 RVC 固定版本及 {label} 训练环境，可能下载数 GB。\n\nRVC 代码采用 MIT；PyTorch、预训练权重和其他依赖遵循各自许可证。继续表示你接受并确认有权训练所用声音素材。",
            T("安装训练组件"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;
        if (_supervisorPath is null) return;
        var process = new Process
        {
            StartInfo = CreateStartInfo(_supervisorPath, ["training", "install", "--backend", backend, "--accept-licenses"], redirectInput: false),
            EnableRaisingEvents = true
        };
        _trainingProcess = process;
        SetTrainingButtons(true);
        var installButton = backend == "cuda" ? _installCudaTrainingButton : _installCpuTrainingButton;
        var originalButtonText = installButton.Content;
        installButton.Content = "正在下载并安装…";
        _trainingProgressText.Text = $"正在准备 {label} 训练环境";
        _trainingProgress.IsIndeterminate = true;
        FooterStatus.Text = $"正在准备 {label} 训练环境，请勿关闭 FoxVoice";
        await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        try
        {
            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("FOXVOICE_TRAINING_STAGE="))
                    UpdateTrainingStage(line["FOXVOICE_TRAINING_STAGE=".Length..]);
            }
            await process.WaitForExitAsync();
            var output = await outputTask;
            if (process.ExitCode != 0) throw new InvalidOperationException("训练组件安装未完成，请查看上方最后阶段后重试");
            using var _ = JsonDocument.Parse(output);
            await RefreshTrainingStatusAsync();
            FooterStatus.Text = $"{label} 训练组件安装并自检通过";
        }
        catch (Exception) when (!ReferenceEquals(_trainingProcess, process))
        {
            FooterStatus.Text = "训练组件安装已取消；下次安装会复用已下载内容";
        }
        catch (Exception error)
        {
            var message = FriendlyError(error);
            _trainingProgressText.Text = "训练环境安装失败";
            FooterStatus.Text = message;
            MessageBox.Show(this, message, "训练环境安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_trainingProcess, process)) _trainingProcess = null;
            process.Dispose();
            _trainingProgress.IsIndeterminate = false;
            installButton.Content = originalButtonText;
            SetTrainingButtons(false);
        }
    }

    private void StopTrainingProcess()
    {
        var process = _trainingProcess;
        _trainingProcess = null;
        if (process is { HasExited: false })
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        _trainingProgress.IsIndeterminate = false;
        _trainingProgressText.Text = "任务已停止，已有检查点会保留";
        SetTrainingButtons(false);
    }

    private void SetTrainingButtons(bool running)
    {
        _installCudaTrainingButton.IsEnabled = !running;
        _installCpuTrainingButton.IsEnabled = !running;
        _cancelTrainingButton.IsEnabled = running;
        _launchTrainingButton.IsEnabled = !running && _trainingReady;
        _importTrainingButton.IsEnabled = !running && _trainingReady;
        _startTrainingButton.IsEnabled = !running && _trainingReady;
        _browseTrainingDatasetButton.IsEnabled = !running;
    }

    private void BrowseTrainingDataset()
    {
        var dialog = new OpenFolderDialog { Title = T("选择已获授权的训练音频目录"), Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            _trainingDatasetText.Text = dialog.FolderName;
            UpdateTrainingDatasetSummary(dialog.FolderName);
        }
    }

    private void UpdateTrainingDatasetSummary(string dataset)
    {
        try
        {
            var files = Directory.EnumerateFiles(dataset, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase))
                .Take(10001).ToList();
            var bytes = files.Sum(path => new FileInfo(path).Length);
            _trainingDatasetSummary.Text = files.Count == 0
                ? "未找到可训练的 WAV/FLAC 音频"
                : $"已找到 {(files.Count > 10000 ? "10000+" : files.Count)} 个音频 · 约 {bytes / 1024d / 1024d:F1} MB";
            _trainingDatasetSummary.Foreground = files.Count == 0
                ? new SolidColorBrush(Color.FromRgb(249, 200, 106))
                : (Brush)FindResource("SuccessBrush");
        }
        catch (Exception)
        {
            _trainingDatasetSummary.Text = "无法读取该文件夹，请检查访问权限";
            _trainingDatasetSummary.Foreground = new SolidColorBrush(Color.FromRgb(249, 200, 106));
        }
    }

    private void UpdateTrainingStage(string stage)
    {
        var numberedStage = stage.Length >= 3 && stage[1] == '/' && stage[2] == '4';
        var terminalStage = stage.Contains("完成", StringComparison.Ordinal) || stage.Contains("失败", StringComparison.Ordinal);
        _trainingProgress.IsIndeterminate = !numberedStage && !terminalStage;
        _trainingProgressText.Text = stage;
        _trainingProgress.Value = stage.StartsWith("1/4", StringComparison.Ordinal) ? 10
            : stage.StartsWith("2/4", StringComparison.Ordinal) ? 30
            : stage.StartsWith("3/4", StringComparison.Ordinal) ? 55
            : stage.StartsWith("4/4", StringComparison.Ordinal) ? 85
            : stage.Contains("完成", StringComparison.Ordinal) ? 100 : _trainingProgress.Value;
        FooterStatus.Text = stage;
    }

    private async Task RunNativeTrainingAsync()
    {
        if (RejectHeavyWorkDuringGame("模型训练")) return;
        if (_trainingProcess is { HasExited: false } || _supervisorPath is null) return;
        var dataset = _trainingDatasetText.Text.Trim();
        var name = _trainingNameText.Text.Trim();
        if (!Directory.Exists(dataset)) { FooterStatus.Text = "请选择有效的训练数据集目录"; return; }
        var hasAudio = Directory.EnumerateFiles(dataset, "*", SearchOption.AllDirectories).Any(path => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase));
        if (!hasAudio) { FooterStatus.Text = "数据集中没有找到 WAV 或 FLAC 音频"; UpdateTrainingDatasetSummary(dataset); return; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,64}$")) { FooterStatus.Text = "实验名称只能包含英文、数字、下划线或连字符"; return; }
        if (!ushort.TryParse(_trainingEpochsText.Text, out var epochs) || epochs is < 1 or > 1200) { FooterStatus.Text = "训练轮数必须为 1-1200"; return; }
        if (!byte.TryParse(_trainingBatchText.Text, out var batch) || batch is < 1 or > 64) { FooterStatus.Text = "批大小必须为 1-64"; return; }
        var answer = MessageBox.Show(this, T("训练会长时间占用 CPU/GPU。请确认数据集中的声音均已获得授权，训练期间不要启动游戏。"), T("开始一键训练"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        var workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 16);
        var process = new Process
        {
            StartInfo = CreateStartInfo(_supervisorPath, ["training", "run", "--dataset", dataset, "--name", name, "--epochs", epochs.ToString(), "--batch", batch.ToString(), "--workers", workers.ToString()], redirectInput: false),
            EnableRaisingEvents = true
        };
        _trainingProcess = process;
        SetTrainingButtons(true);
        _trainingProgress.Value = 2;
        _trainingProgressText.Text = "正在准备训练任务";
        var lastError = "";
        try
        {
            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lastError = line;
                if (line.StartsWith("FOXVOICE_TRAINING_STAGE="))
                    UpdateTrainingStage(line["FOXVOICE_TRAINING_STAGE=".Length..]);
            }
            await process.WaitForExitAsync();
            await outputTask;
            if (process.ExitCode != 0) throw new InvalidOperationException(lastError.Length == 0 ? "训练任务失败" : lastError);
            UpdateTrainingStage("训练完成；正在导入输出模型");
            await ImportTrainingOutputsAsync(confirm: false);
        }
        catch (Exception) when (!ReferenceEquals(_trainingProcess, process)) { FooterStatus.Text = "训练已停止；已有检查点未删除"; }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
        finally
        {
            if (ReferenceEquals(_trainingProcess, process)) _trainingProcess = null;
            process.Dispose();
            SetTrainingButtons(false);
        }
    }

    private async Task LaunchTrainingWorkbenchAsync()
    {
        if (RejectHeavyWorkDuringGame("模型训练")) return;
        if (_trainingProcess is { HasExited: false } || _supervisorPath is null) return;
        var process = new Process
        {
            StartInfo = CreateStartInfo(_supervisorPath, ["training", "workbench"], redirectInput: false),
            EnableRaisingEvents = true
        };
        _trainingProcess = process;
        SetTrainingButtons(true);
        _trainingProgressText.Text = "训练工作台正在启动";
        _trainingProgress.IsIndeterminate = true;
        var lastError = "";
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            lastError = args.Data;
            Dispatcher.BeginInvoke(() => FooterStatus.Text = args.Data.StartsWith("FOXVOICE_TRAINING_STAGE=")
                ? args.Data["FOXVOICE_TRAINING_STAGE=".Length..] : "训练工作台正在运行");
        };
        try
        {
            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            for (var attempt = 0; attempt < 60 && !process.HasExited; attempt++)
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    await client.ConnectAsync("127.0.0.1", 7865);
                    Process.Start(new ProcessStartInfo("http://127.0.0.1:7865") { UseShellExecute = true });
                    FooterStatus.Text = "RVC 训练工作台已打开；训练期间不要启动游戏";
                    break;
                }
                catch (System.Net.Sockets.SocketException) { await Task.Delay(500); }
            }
            await process.WaitForExitAsync();
            if (ReferenceEquals(_trainingProcess, process) && process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastError) ? "训练工作台异常退出" : lastError);
        }
        catch (Exception) when (!ReferenceEquals(_trainingProcess, process))
        {
            FooterStatus.Text = "训练工作台已停止；已保存的检查点不会删除";
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
        finally
        {
            if (ReferenceEquals(_trainingProcess, process)) _trainingProcess = null;
            process.Dispose();
            _trainingProgress.IsIndeterminate = false;
            SetTrainingButtons(false);
        }
    }

    private async Task ImportTrainingOutputsAsync(bool confirm = true)
    {
        if (RejectHeavyWorkDuringGame("训练结果导入")) return;
        try
        {
            using var document = JsonDocument.Parse(await RunSupervisorAsync("training", "outputs"));
            var paths = document.RootElement.EnumerateArray()
                .Select(value => value.GetProperty("path").GetString()).OfType<string>().ToList();
            if (paths.Count == 0)
            {
                FooterStatus.Text = "训练工作台尚未生成 .pth 或 .index 结果";
                return;
            }
            if (confirm)
            {
                var answer = MessageBox.Show(this, $"发现 {paths.Count} 个训练结果。将全部经过哈希和格式门禁后导入模型库。",
                    "导入训练结果", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (answer != MessageBoxResult.OK) return;
            }
            foreach (var path in paths) await RunSupervisorAsync("models", "import", path, "--rights-confirmed");
            await RefreshModelsAsync();
            ModelsNav.IsChecked = true;
            FooterStatus.Text = $"已导入 {paths.Count} 个训练结果；RVC v2 F0 检查点可点击“使用”转换为 ONNX";
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
    }

    private void BuildSoundboardView()
    {
        _settings.SoundboardFiles = _settings.SoundboardFiles
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
        _settings.SoundboardLoopFiles = _settings.SoundboardLoopFiles
            .Where(path => _settings.SoundboardFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _settings.SoundboardGroups = _settings.SoundboardGroups
            .Where(pair => _settings.SoundboardFiles.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => NormalizeSoundGroup(pair.Value), StringComparer.OrdinalIgnoreCase);
        _soundboardGain.Value = Math.Clamp(_settings.SoundboardGainDb, -36, 12);
        _soundboardGain.ValueChanged += (_, _) =>
        {
            if (!_ready) return;
            _settings.SoundboardGainDb = _soundboardGain.Value;
            TrySaveSettings();
        };
        SoundboardView.Children.Clear();
        var root = new Grid { Margin = new Thickness(28, 26, 28, 26) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "SOUNDBOARD", Style = (Style)FindResource("Eyebrow") });
        title.Children.Add(new TextBlock { Text = "音效板", Style = (Style)FindResource("SectionTitle") });
        _soundboardGroupFilter.ItemsSource = SoundGroups.Prepend("全部").ToList();
        _soundboardGroupFilter.SelectedIndex = 0;
        _soundboardGroupFilter.SelectionChanged += (_, _) => RefreshSoundboardCards();
        var add = new Button { Content = "＋ 添加音效", Style = (Style)FindResource("PrimaryButton") };
        add.Click += AddSound_Click;
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        headerActions.Children.Add(_soundboardGroupFilter);
        headerActions.Children.Add(add);
        var header = new Grid();
        header.Children.Add(title);
        header.Children.Add(headerActions);
        root.Children.Add(header);

        var mixer = new Border { Style = (Style)FindResource("Panel"), Margin = new Thickness(0, 18, 0, 16), Padding = new Thickness(18) };
        var mixerGrid = new Grid();
        mixerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        mixerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock { Text = "共享输出混音", FontWeight = FontWeights.SemiBold });
        copy.Children.Add(new TextBlock { Text = "音效发送到当前主输出；选择 VB-CABLE 时会与变声一起进入游戏", Foreground = (Brush)FindResource("TextSecondary") });
        var gain = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        gain.Children.Add(new TextBlock { Text = "音效增益", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        gain.Children.Add(_soundboardGain);
        Grid.SetColumn(gain, 1);
        mixerGrid.Children.Add(copy);
        mixerGrid.Children.Add(gain);
        mixer.Child = mixerGrid;
        Grid.SetRow(mixer, 1);
        root.Children.Add(mixer);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _soundboardPanel };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        SoundboardView.Children.Add(root);
        RefreshSoundboardCards();
    }

    private void AddSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = T("添加音效"),
            Filter = UiText.IsEnglish(UiText.CurrentLanguage)
                ? "Supported audio (*.wav;*.flac;*.mp3;*.ogg)|*.wav;*.flac;*.mp3;*.ogg|All files (*.*)|*.*"
                : "支持的音频 (*.wav;*.flac;*.mp3;*.ogg)|*.wav;*.flac;*.mp3;*.ogg|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
        {
            if (_settings.SoundboardFiles.Count >= 24) break;
            if (!_settings.SoundboardFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _settings.SoundboardFiles.Add(path);
                _settings.SoundboardGroups[path] = "常用";
            }
        }
        TrySaveSettings();
        RefreshSoundboardCards();
    }

    private void RefreshSoundboardCards()
    {
        _soundboardPanel.Children.Clear();
        var selectedGroup = _soundboardGroupFilter.SelectedItem as string ?? "全部";
        foreach (var (path, index) in _settings.SoundboardFiles.ToList().Select((path, index) => (path, index)))
        {
            if (!File.Exists(path)) { _settings.SoundboardFiles.Remove(path); continue; }
            var group = _settings.SoundboardGroups.TryGetValue(path, out var configuredGroup) ? NormalizeSoundGroup(configuredGroup) : "常用";
            if (selectedGroup != "全部" && selectedGroup != group) continue;
            var card = new Border { Style = (Style)FindResource("Panel"), Width = 220, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = Path.GetFileName(path), Foreground = (Brush)FindResource("TextSecondary"), FontSize = 10, Margin = new Thickness(0, 3, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis });
            if (index < 8) stack.Children.Add(new TextBlock { Text = $"Ctrl + Alt + F{index + 1}", Foreground = (Brush)FindResource("AccentBrush"), FontSize = 10, Margin = new Thickness(0, 0, 0, 8) });
            var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
            var groupBox = new ComboBox { ItemsSource = SoundGroups, SelectedItem = group, Width = 92, Tag = path };
            groupBox.SelectionChanged += SoundGroup_Changed;
            var loop = new CheckBox { Content = "循环", IsChecked = _settings.SoundboardLoopFiles.Contains(path, StringComparer.OrdinalIgnoreCase), Tag = path, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            loop.Checked += SoundLoop_Changed;
            loop.Unchecked += SoundLoop_Changed;
            options.Children.Add(groupBox);
            options.Children.Add(loop);
            stack.Children.Add(options);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var play = new Button { Content = "▶ 播放", Tag = path, Padding = new Thickness(12, 6, 12, 6) };
            play.Click += PlaySound_Click;
            var remove = new Button { Content = "移除", Tag = path, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += RemoveSound_Click;
            actions.Children.Add(play);
            actions.Children.Add(remove);
            stack.Children.Add(actions);
            card.Child = stack;
            _soundboardPanel.Children.Add(card);
        }
        if (_soundboardPanel.Children.Count == 0)
            _soundboardPanel.Children.Add(new TextBlock { Text = "还没有音效。添加 WAV、FLAC、MP3 或 OGG 后，可发送到当前主输出。", Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(8, 28, 0, 0) });
        RegisterSoundboardHotkeys();
    }

    private void RemoveSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        _settings.SoundboardFiles.RemoveAll(value => value.Equals(path, StringComparison.OrdinalIgnoreCase));
        _settings.SoundboardLoopFiles.RemoveAll(value => value.Equals(path, StringComparison.OrdinalIgnoreCase));
        _settings.SoundboardGroups.Remove(path);
        StopLoopingSound(path);
        TrySaveSettings();
        RefreshSoundboardCards();
    }

    private void PlaySound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) PlaySound(path);
    }

    private void PlaySound(string path, long? startMs = null, long? endMs = null, bool allowLoop = true)
    {
        if (_enginePath is null || !File.Exists(path)) return;
        var looping = allowLoop && _settings.SoundboardLoopFiles.Contains(path, StringComparer.OrdinalIgnoreCase);
        if (looping && _loopingSoundProcesses.ContainsKey(path))
        {
            StopLoopingSound(path);
            FooterStatus.Text = $"已停止循环：{Path.GetFileNameWithoutExtension(path)}";
            return;
        }
        var arguments = new List<string> { "play-audio", "--file", path, "--gain-db", _soundboardGain.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) };
        if (looping) arguments.Add("--loop");
        if (startMs.HasValue && endMs.HasValue) arguments.AddRange(["--start-ms", startMs.Value.ToString(), "--end-ms", endMs.Value.ToString()]);
        if (OutputDeviceCombo.SelectedItem is AudioDevice output) arguments.AddRange(["--output", output.Name]);
        var process = new Process { StartInfo = CreateStartInfo(_enginePath, arguments, redirectInput: false), EnableRaisingEvents = true };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) Dispatcher.BeginInvoke(() => FooterStatus.Text = $"音效播放失败：{args.Data}");
        };
        process.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _soundProcesses.Remove(process);
            if (_loopingSoundProcesses.TryGetValue(path, out var active) && ReferenceEquals(active, process))
                _loopingSoundProcesses.Remove(path);
            process.Dispose();
        });
        try
        {
            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _soundProcesses.Add(process);
            if (looping) _loopingSoundProcesses[path] = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            FooterStatus.Text = $"正在播放音效：{Path.GetFileNameWithoutExtension(path)}";
        }
        catch (Exception error) { process.Dispose(); FooterStatus.Text = FriendlyError(error); }
    }

    private static readonly string[] SoundGroups = ["常用", "语音", "音乐", "其他"];

    private static string NormalizeSoundGroup(string? group) => SoundGroups.Contains(group) ? group! : "常用";

    private void SoundGroup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string path, SelectedItem: string group }) return;
        _settings.SoundboardGroups[path] = NormalizeSoundGroup(group);
        TrySaveSettings();
    }

    private void SoundLoop_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string path } toggle) return;
        _settings.SoundboardLoopFiles.RemoveAll(value => value.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (toggle.IsChecked == true) _settings.SoundboardLoopFiles.Add(path);
        else StopLoopingSound(path);
        TrySaveSettings();
    }

    private void StopLoopingSound(string path)
    {
        if (!_loopingSoundProcesses.Remove(path, out var process)) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private void StopSoundboardProcesses()
    {
        foreach (var process in _soundProcesses.ToList())
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch { } }
            process.Dispose();
        }
        _soundProcesses.Clear();
        _loopingSoundProcesses.Clear();
    }

    private void InitializeSoundboardHotkeys()
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(SoundboardWindowHook);
        RegisterSoundboardHotkeys();
    }

    private void RegisterSoundboardHotkeys()
    {
        if (_windowHandle == IntPtr.Zero) return;
        for (var index = 0; index < 8; index++)
        {
            UnregisterHotKey(_windowHandle, 0x5100 + index);
            if (index < _settings.SoundboardFiles.Count)
                RegisterHotKey(_windowHandle, 0x5100 + index, 0x0001 | 0x0002 | 0x4000, (uint)(0x70 + index));
        }
    }

    private void ReleaseSoundboardHotkeys()
    {
        if (_windowHandle == IntPtr.Zero) return;
        for (var index = 0; index < 8; index++) UnregisterHotKey(_windowHandle, 0x5100 + index);
        _windowSource?.RemoveHook(SoundboardWindowHook);
    }

    private IntPtr SoundboardWindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312) return IntPtr.Zero;
        var index = wParam.ToInt32() - 0x5100;
        if ((uint)index < (uint)Math.Min(8, _settings.SoundboardFiles.Count))
        {
            PlaySound(_settings.SoundboardFiles[index]);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
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
        ApplyLocalization();
    }

    private void LanguageSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        _settings.Language = UiText.IsEnglish(language) ? UiText.English : UiText.Chinese;
        ModelsList.ItemsSource = null;
        ModelsList.ItemsSource = _models;
        PresetItems.ItemsSource = null;
        PresetItems.ItemsSource = _models.Where(model => model.IsUsable).Take(4).ToList();
        ApplyLocalization();
        if (IsLoaded) TrySaveSettings();
    }

    private void ApplyLocalization() => UiText.Apply(this, _settings.Language);

    private static string T(string value) => UiText.Translate(value);

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
        ApplyLocalization();
    }

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
                if (RejectHeavyWorkDuringGame("模型转换")) return;
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
        if (_selectedModel is not null) SaveCurrentModelProfile();
        _selectedModel = model?.IsUsable == true ? model : null;
        ActiveModelText.Text = _selectedModel?.DisplayName ?? "尚未选择";
        if (_selectedModel is not null)
        {
            ModelsList.SelectedItem = _selectedModel;
            ApplyCurrentModelProfile();
        }
        if (persist)
        {
            _settings.SelectedModelId = _selectedModel?.Id ?? "";
            TrySaveSettings();
            if (_selectedModel is not null) _ = MarkModelUsedAsync(_selectedModel.Id);
        }
    }

    private async Task MarkModelUsedAsync(string id)
    {
        try
        {
            await _modelActivityLock.WaitAsync();
            await RunSupervisorAsync("models", "used", id);
        }
        catch { /* 最近使用时间不应阻断音频主流程。 */ }
        finally { _modelActivityLock.Release(); }
    }

    private void AddModelRecycleAction()
    {
        var importButton = FindDescendant<Button>(ModelsView, button => Equals(button.Content, "＋ 导入本地模型"));
        if (importButton?.Parent is not StackPanel actions) return;
        var recycleButton = new Button { Content = "移入回收区", Margin = new Thickness(0, 0, 8, 0) };
        recycleButton.Click += RecycleModel_Click;
        var metadataButton = new Button { Content = "编辑资料", Margin = new Thickness(0, 0, 8, 0) };
        metadataButton.Click += EditModelMetadata_Click;
        var offlineButton = new Button { Content = "离线编辑", Margin = new Thickness(0, 0, 8, 0) };
        offlineButton.Click += ConvertWav_Click;
        var insertAt = Math.Max(0, actions.Children.IndexOf(importButton));
        actions.Children.Insert(insertAt, offlineButton);
        actions.Children.Insert(insertAt + 1, metadataButton);
        actions.Children.Insert(insertAt + 2, recycleButton);
    }

    private async void EditModelMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsList.SelectedItem is not ModelItem model)
        {
            FooterStatus.Text = "请先选择要编辑资料的模型";
            return;
        }
        var dialog = new ModelMetadataDialog(this, model.DisplayName, model.Author, model.License, model.Tags);
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _modelActivityLock.WaitAsync();
            try
            {
                await RunSupervisorAsync("models", "metadata", model.Id, dialog.ModelName,
                    dialog.Author, dialog.License, dialog.Tags);
                await RefreshModelsAsync();
            }
            finally { _modelActivityLock.Release(); }
            ModelsList.SelectedItem = _models.FirstOrDefault(item => item.Id == model.Id);
            FooterStatus.Text = "模型资料已保存";
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
    }

    private void AddModelTransferCancelAction()
    {
        _cancelModelTransferButton.Click += (_, _) => StopModelTransfer();
        var download = FindDescendant<Button>(ModelsView, button => Equals(button.Content, "下载并导入"));
        if (download?.Parent is not Grid row) return;
        var column = new ColumnDefinition { Width = GridLength.Auto };
        row.ColumnDefinitions.Add(column);
        Grid.SetColumn(_cancelModelTransferButton, row.ColumnDefinitions.Count - 1);
        row.Children.Add(_cancelModelTransferButton);
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match)) return match;
            var nested = FindDescendant(child, predicate);
            if (nested is not null) return nested;
        }
        return null;
    }

    private async void RecycleModel_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsList.SelectedItem is not ModelItem model)
        {
            FooterStatus.Text = "请先在模型库选择要移入回收区的模型";
            return;
        }
        if (_audioProcess is { HasExited: false } && _selectedModel?.Id == model.Id)
        {
            FooterStatus.Text = "当前模型正在使用；请先停止实时变声再移除";
            return;
        }
        var answer = MessageBox.Show(this,
            UiText.IsEnglish(UiText.CurrentLanguage)
                ? $"Move “{model.DisplayName}” to the FoxVoice recycle area. Files are not permanently deleted immediately."
                : $"将“{model.DisplayName}”移入 FoxVoice 回收区。文件不会立即永久删除。",
            T("移除模型"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            await RunSupervisorAsync("models", "recycle", model.Id);
            if (_selectedModel?.Id == model.Id) SelectModel(null, persist: true);
            await RefreshModelsAsync();
            FooterStatus.Text = "模型已安全移入回收区";
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
    }

    private async void ConvertWav_Click(object sender, RoutedEventArgs e)
    {
        if (RejectHeavyWorkDuringGame("离线音频转换")) return;
        if (_audioProcess is { HasExited: false })
        {
            FooterStatus.Text = "请先停止实时变声，避免离线转换与游戏语音争用 GPU";
            return;
        }
        if (_selectedModel is null || !File.Exists(EmbedderPath.Text) || !File.Exists(F0Path.Text))
        {
            FooterStatus.Text = "离线转换需要先选择可用 Generator，并安装 ContentVec 与 RMVPE";
            return;
        }
        try
        {
            var inputDialog = new OpenFileDialog { Title = T("选择待转换音频"), Filter = UiText.IsEnglish(UiText.CurrentLanguage) ? "Supported audio (*.wav;*.flac;*.mp3;*.ogg)|*.wav;*.flac;*.mp3;*.ogg" : "支持的音频 (*.wav;*.flac;*.mp3;*.ogg)|*.wav;*.flac;*.mp3;*.ogg" };
            if (inputDialog.ShowDialog(this) != true) return;
            using var waveform = JsonDocument.Parse(await RunEngineCommandAsync("waveform", "--file", inputDialog.FileName, "--points", "180"));
            var durationMs = waveform.RootElement.GetProperty("durationMs").GetDouble();
            var peaks = waveform.RootElement.GetProperty("peaks").EnumerateArray().Select(value => value.GetDouble()).ToList();
            var trim = new OfflineTrimDialog(this, inputDialog.FileName, durationMs, peaks,
                (start, end) => PlaySound(inputDialog.FileName, start, end, allowLoop: false));
            if (trim.ShowDialog() != true) return;
            var outputDialog = new SaveFileDialog
            {
                Title = T("保存变声音频"), Filter = UiText.IsEnglish(UiText.CurrentLanguage) ? "WAV audio (*.wav)|*.wav|FLAC lossless audio (*.flac)|*.flac" : "WAV 音频 (*.wav)|*.wav|FLAC 无损音频 (*.flac)|*.flac", AddExtension = true,
                DefaultExt = ".wav", FileName = Path.GetFileNameWithoutExtension(inputDialog.FileName) + "-foxvoice"
            };
            if (outputDialog.ShowDialog(this) != true) return;
            FooterStatus.Text = "正在离线转换所选音频区间；实时语音保持停用…";
            using var resolved = JsonDocument.Parse(await RunSupervisorAsync("models", "resolve", _selectedModel.Id));
            var modelPath = resolved.RootElement.GetProperty("path").GetString()
                ?? throw new InvalidOperationException("模型路径解析失败");
            var convertArguments = new List<string> {
                "convert-audio", "--input", inputDialog.FileName, "--output", outputDialog.FileName,
                "--model", modelPath, "--embedder", EmbedderPath.Text, "--f0", F0Path.Text,
                "--pitch", PitchSlider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--start-ms", trim.StartMs.ToString(), "--end-ms", trim.EndMs.ToString()
            };
            AddFeatureIndexArguments(convertArguments);
            using var result = JsonDocument.Parse(await RunEngineCommandAsync(convertArguments.ToArray()));
            var elapsed = result.RootElement.GetProperty("elapsedMs").GetDouble();
            FooterStatus.Text = $"离线转换完成：{Path.GetFileName(outputDialog.FileName)}（{elapsed / 1000:N1} 秒）";
            if (MessageBox.Show(this, T("转换完成。是否立即试听结果？"), T("离线转换"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                PlaySound(outputDialog.FileName, allowLoop: false);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{outputDialog.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception error) { FooterStatus.Text = $"离线转换失败：{FriendlyError(error)}"; }
    }

    private async void ImportModel_Click(object sender, RoutedEventArgs e)
    {
        if (RejectHeavyWorkDuringGame("模型导入与转换")) return;
        var permission = MessageBox.Show(this,
            T("导入前请确认：你有权使用和转换所选模型及索引，并会遵守模型发布者声明的许可证。FoxVoice 只在本机保存文件，不会替你获得或验证模型授权。"),
            T("确认本地模型来源"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (permission != MessageBoxResult.OK) return;
        var dialog = new OpenFileDialog
        {
            Title = T("导入 RVC 模型或索引"),
            Filter = "RVC 文件 (*.onnx;*.pth;*.index)|*.onnx;*.pth;*.index",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportModelAsync(["models", "import", dialog.FileName, "--rights-confirmed"], "正在校验并导入本地模型…");
    }

    private async void ImportHuggingFace_Click(object sender, RoutedEventArgs e)
    {
        if (RejectHeavyWorkDuringGame("模型下载")) return;
        var url = HuggingFaceUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            FooterStatus.Text = "请输入 Hugging Face 的 HTTPS resolve 文件地址";
            return;
        }
        if (url.Contains("/resolve/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var infoDocument = JsonDocument.Parse(await RunSupervisorAsync("models", "huggingface-info", url));
                var info = HuggingFaceRepositoryInfo.FromJson(infoDocument.RootElement);
                if (info.Private || info.Gated)
                {
                    FooterStatus.Text = "该 Hugging Face 仓库是私有或受限仓库；FoxVoice 当前不接收访问令牌";
                    return;
                }
                var answer = MessageBox.Show(this,
                    $"来源：{info.Repository}\n分支：{info.Revision}\n许可证：{info.License ?? "未声明"}\n\n请确认你有权下载和使用此模型。",
                    T("确认模型来源"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (answer != MessageBoxResult.OK) return;
            }
            catch (Exception error) { FooterStatus.Text = FriendlyError(error); return; }
            await ImportHuggingFaceModelAsync(url, "正在从 Hugging Face 下载并校验…");
            return;
        }
        try
        {
            FooterStatus.Text = "正在读取 Hugging Face 仓库文件…";
            using var infoDocument = JsonDocument.Parse(await RunSupervisorAsync("models", "huggingface-info", url));
            var repositoryInfo = HuggingFaceRepositoryInfo.FromJson(infoDocument.RootElement);
            if (repositoryInfo.Private || repositoryInfo.Gated)
            {
                FooterStatus.Text = "该 Hugging Face 仓库是私有或受限仓库；FoxVoice 当前不接收访问令牌";
                return;
            }
            using var document = JsonDocument.Parse(await RunSupervisorAsync("models", "huggingface-files", url));
            var files = document.RootElement.EnumerateArray().Select(HuggingFaceFileItem.FromJson).ToList();
            if (files.Count == 0)
            {
                FooterStatus.Text = "此仓库没有 .onnx、.pth 或 .index 文件";
                return;
            }
            var selected = ChooseHuggingFaceFile(files, repositoryInfo);
            if (selected is null) { FooterStatus.Text = "已取消仓库导入"; return; }
            HuggingFaceUrl.Text = selected.DownloadUrl;
            await ImportHuggingFaceModelAsync(selected.DownloadUrl, $"正在下载 {selected.Path} 并校验…");
        }
        catch (Exception error) { FooterStatus.Text = FriendlyError(error); }
    }

    private HuggingFaceFileItem? ChooseHuggingFaceFile(IReadOnlyList<HuggingFaceFileItem> files, HuggingFaceRepositoryInfo info)
    {
        HuggingFaceFileItem? selected = null;
        var list = new ListBox { ItemsSource = files, DisplayMemberPath = nameof(HuggingFaceFileItem.DisplayLabel), Margin = new Thickness(0, 14, 0, 14) };
        list.SelectedIndex = 0;
        var dialog = new Window
        {
            Owner = this, Title = T("选择 Hugging Face 模型文件"), Width = 680, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("AppBackground"),
            Foreground = (Brush)FindResource("TextPrimary"), ResizeMode = ResizeMode.CanResizeWithGrip
        };
        var root = new DockPanel { Margin = new Thickness(22) };
        var title = new TextBlock { Text = "仓库中可导入的模型文件", FontSize = 20, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        var license = new TextBlock
        {
            Text = $"{info.Repository} · {info.Revision} · 许可证：{info.License ?? "未声明"}",
            Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 5, 0, 0)
        };
        DockPanel.SetDock(license, Dock.Top);
        root.Children.Add(license);
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

    private async Task ImportHuggingFaceModelAsync(string url, string progress)
    {
        try
        {
            FooterStatus.Text = progress + "（可取消并续传）";
            await RunCancelableModelTransferAsync("models", "huggingface", url, "--rights-confirmed");
            await RefreshModelsAsync();
            FooterStatus.Text = "Hugging Face 模型下载、校验和导入完成";
        }
        catch (OperationCanceledException)
        {
            FooterStatus.Text = "模型下载已暂停；再次下载同一地址会从已保存断点继续";
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
            AddFeatureIndexArguments(arguments);
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
            "--provider", _settings.PreferredProvider is "nvtrtx" ? "nvtrtx" : "directml",
            "--pitch", PitchSlider.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            "--output-gain-db", OutputGainSlider.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        };
        if (NoiseGateToggle.IsChecked == true) arguments.Add("--noise-gate");
        if (InputDeviceCombo.SelectedItem is AudioDevice input) arguments.AddRange(["--input", input.Name]);
        if (OutputDeviceCombo.SelectedItem is AudioDevice output) arguments.AddRange(["--output", output.Name]);
        return arguments;
    }

    private void AddFeatureIndexArguments(List<string> arguments)
    {
        var profile = _selectedModel is not null && _settings.ModelProfiles.TryGetValue(_selectedModel.Id, out var value)
            ? value
            : new ModelProfile { FeatureIndexPath = _settings.FeatureIndexPath, IndexRate = _settings.IndexRate, Protect = _settings.Protect };
        if (!File.Exists(profile.FeatureIndexPath)) return;
        arguments.AddRange([
            "--index", profile.FeatureIndexPath,
            "--index-rate", profile.IndexRate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            "--protect", profile.Protect.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        ]);
        if (profile.F0Smoothing) arguments.Add("--f0-smoothing");
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
        StreamErrorText.Text = (snapshot.InputOverruns + snapshot.OutputDroppedSamples).ToString("N0");
        UpdateGameGuard(snapshot);
        if (snapshot.ProcessingUs > 0)
        {
            var milliseconds = snapshot.ProcessingUs / 1000.0;
            var budget = snapshot.ChunkMs > 0 ? snapshot.ChunkMs : DefaultProcessingBudgetMs;
            LatencyText.Text = $"{milliseconds:0.0} ms";
            BudgetText.Text = $"{milliseconds:0.0} / {budget:0} ms";
            BudgetProgress.Value = Math.Clamp(milliseconds / budget * 100.0, 0, 100);
        }
        ApplyLocalization();
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
        ApplyLocalization();
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
        ApplyLocalization();
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
        var dataFlowErrors = snapshot.InputOverruns + snapshot.OutputDroppedSamples;
        var newFault = snapshot.OutputUnderruns > _guardLastUnderruns || dataFlowErrors > _guardLastStreamErrors;
        _guardLastUnderruns = snapshot.OutputUnderruns;
        _guardLastStreamErrors = dataFlowErrors;
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
            next = _guardProfile switch { "bypass" => "survival", "survival" => "stable", "stable" when !_gameDetected => "normal", _ => _guardProfile };
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
                "stable" => "稳定档 · 保持当前链路",
                "survival" => "保生存档 · 无缝原声旁路",
                "bypass" => "保护旁路 · 等待恢复",
                _ => "资源保护已启用"
            };
            FooterStatus.Text = level switch
            {
                "stable" => "检测到连续超载：保持已加载链路并观察恢复，不重载模型",
                "survival" => "负载仍高：已无缝切到原声旁路，避免游戏语音中断",
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
        if (RejectHeavyWorkDuringGame("模型自检")) return;
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
            FooterStatus.Text = "正在用 WindowsML/DirectML 加载三模型并执行预热与 20 帧基准…";
            using var resolved = JsonDocument.Parse(await RunSupervisorAsync("models", "resolve", _selectedModel.Id));
            var modelPath = resolved.RootElement.GetProperty("path").GetString()
                ?? throw new InvalidOperationException("模型路径解析失败");
            var raw = await RunEngineCommandAsync("validate-rvc", "--frames", "20", "--model", modelPath,
                "--embedder", EmbedderPath.Text, "--f0", F0Path.Text);
            using var report = JsonDocument.Parse(raw);
            var root = report.RootElement;
            var loadMs = root.GetProperty("loadMs").GetDouble();
            var inferenceMs = root.GetProperty("inferenceMs").GetDouble();
            var p95Ms = root.GetProperty("p95Ms").GetDouble();
            var p99Ms = root.GetProperty("p99Ms").GetDouble();
            var outputSamples = root.GetProperty("outputSamples").GetInt32();
            await RecordModelTestAsync(_selectedModel.Id, true, "directml");
            DoctorText.Text = $"RVC 三模型自检通过\n\n后端：WindowsML / DirectML\n模型加载：{loadMs:N0} ms\n20 帧平均：{inferenceMs:N1} ms\nP95 / P99：{p95Ms:N1} / {p99Ms:N1} ms\n输出采样：{outputSamples:N0}\n\n" + DoctorText.Text;
            FooterStatus.Text = $"RVC 自检通过：平均 {inferenceMs:N1} ms，P95 {p95Ms:N1} ms，P99 {p99Ms:N1} ms";
        }
        catch (Exception error)
        {
            if (_selectedModel is not null) await RecordModelTestAsync(_selectedModel.Id, false, "directml");
            FooterStatus.Text = $"RVC 自检失败：{FriendlyError(error)}";
        }
        finally { _rvcSelfTestButton.IsEnabled = true; }
    }

    private async Task RecordModelTestAsync(string id, bool passed, string provider)
    {
        try
        {
            await _modelActivityLock.WaitAsync();
            await RunSupervisorAsync("models", "tested", id, passed ? "passed" : "failed", provider);
        }
        catch { /* 诊断记录失败不能覆盖真实自检结果。 */ }
        finally { _modelActivityLock.Release(); }
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

    private void BrowseEmbedder_Click(object sender, RoutedEventArgs e) => BrowseOnnxInto(EmbedderPath, T("选择 ContentVec ONNX"));
    private void BrowseF0_Click(object sender, RoutedEventArgs e) => BrowseOnnxInto(F0Path, T("选择 RMVPE ONNX"));

    private void BrowseOnnxInto(TextBox target, string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = UiText.IsEnglish(UiText.CurrentLanguage) ? "ONNX model (*.onnx)|*.onnx" : "ONNX 模型 (*.onnx)|*.onnx" };
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
            SaveCurrentModelProfile();
            _settings.GameGuardEnabled = GameGuardToggle.IsChecked == true;
            _settings.Save();
        }
        catch (Exception error)
        {
            FooterStatus.Text = $"设置保存失败：{FriendlyError(error)}";
        }
    }

    private void ApplyCurrentModelProfile()
    {
        if (_selectedModel is null) return;
        if (!_settings.ModelProfiles.TryGetValue(_selectedModel.Id, out var profile))
        {
            profile = new ModelProfile {
                FeatureIndexPath = _settings.FeatureIndexPath,
                IndexRate = _settings.IndexRate,
                Protect = _settings.Protect,
                F0Smoothing = _settings.F0Smoothing
            };
            _settings.ModelProfiles[_selectedModel.Id] = profile;
        }
        IndexRateSlider.Value = Math.Clamp(profile.IndexRate, 0, 1);
        ProtectSlider.Value = Math.Clamp(profile.Protect, 0, 0.5);
        F0SmoothingToggle.IsChecked = profile.F0Smoothing;
        UpdateProfileLabels();
    }

    private void SaveCurrentModelProfile()
    {
        if (_selectedModel is null || !_ready) return;
        _settings.ModelProfiles[_selectedModel.Id] = new ModelProfile {
            FeatureIndexPath = _settings.FeatureIndexPath,
            IndexRate = IndexRateSlider.Value,
            Protect = ProtectSlider.Value,
            F0Smoothing = F0SmoothingToggle.IsChecked == true
        };
        _settings.IndexRate = IndexRateSlider.Value;
        _settings.Protect = ProtectSlider.Value;
        _settings.F0Smoothing = F0SmoothingToggle.IsChecked == true;
    }

    private void ProfileControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _selectedModel is null) return;
        UpdateProfileLabels();
        SaveCurrentModelProfile();
        TrySaveSettings();
        FooterStatus.Text = "模型参数已保存；F0 平滑和索引参数将在下次启动引擎时生效";
    }

    private void UpdateProfileLabels()
    {
        if (IndexRateValueText is not null) IndexRateValueText.Text = IndexRateSlider.Value.ToString("0.00");
        if (ProtectValueText is not null) ProtectValueText.Text = ProtectSlider.Value.ToString("0.00");
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

    private async Task<string> RunCancelableModelTransferAsync(params string[] arguments)
    {
        if (_supervisorPath is null) throw new InvalidOperationException("原生控制服务尚未连接");
        if (_modelTransferProcess is { HasExited: false }) throw new InvalidOperationException("已有模型下载正在运行");
        var process = new Process { StartInfo = CreateStartInfo(_supervisorPath, arguments, redirectInput: false) };
        _modelTransferProcess = process;
        _cancelModelTransferButton.IsEnabled = true;
        try
        {
            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (!ReferenceEquals(_modelTransferProcess, process)) throw new OperationCanceledException();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "模型下载失败" : error.Trim());
            return output;
        }
        finally
        {
            if (ReferenceEquals(_modelTransferProcess, process)) _modelTransferProcess = null;
            _cancelModelTransferButton.IsEnabled = false;
            process.Dispose();
        }
    }

    private void StopModelTransfer()
    {
        var process = _modelTransferProcess;
        _modelTransferProcess = null;
        if (process is { HasExited: false })
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        _cancelModelTransferButton.IsEnabled = false;
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
        try
        {
            if (fileName.Equals("foxvoice-engine.exe", StringComparison.OrdinalIgnoreCase))
                NativeBundle.EnsureExtracted("Microsoft.WindowsAppRuntime.Bootstrap.dll");
            return NativeBundle.EnsureExtracted(fileName);
        }
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

    private sealed record ModelItem(
        string Id, string DisplayName, string Format, string State, long SizeBytes, string Hash,
        string? Author, string? License, IReadOnlyList<string> Tags, string? RvcVersion,
        int? SampleRate, bool? UsesF0, int? SpeakerCount, string? RecommendedProvider,
        string? TestStatus, long? LastTestedAtUnixMs, long? LastUsedAtUnixMs, long? RightsConfirmedAtUnixMs)
    {
        public bool IsUsable => Format == "onnx" && State == "ready";
        public string SizeText => $"{SizeBytes / 1024d / 1024d:N1} MB";
        private string HashPrefix => Hash.Length > 16 ? Hash[..16] : Hash;
        public string ShortHash
        {
            get
            {
                var details = DetailLine;
                var activity = ActivityLine;
                return string.Join(" · ", new[] { details, activity }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }
        public string FormatLabel => Format switch { "onnx" => "ONNX", "pytorchCheckpoint" => "PyTorch", "faissIndex" => "FAISS index", _ => Format };
        public string StateLabel => UiText.Translate(State switch { "ready" => "可使用", "conversionRequired" => "需要转换", "storedOnly" => "仅保存", _ => State }, UiText.CurrentLanguage);
        public string DetailLine => string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(Author) ? null : Author,
            string.IsNullOrWhiteSpace(License) ? null : License,
            Tags.Count == 0 ? null : string.Join(" / ", Tags),
            HashPrefix
        }.Where(value => value is not null));
        public string ActivityLine
        {
            get
            {
                var profile = RvcVersion is null ? null : $"RVC {RvcVersion}";
                var rate = SampleRate is null ? null : $"{SampleRate / 1000d:0.#} kHz";
                var test = TestStatus switch { "passed" => $"{ProviderLabel(RecommendedProvider)}{(UiText.IsEnglish(UiText.CurrentLanguage) ? "self-test passed" : "自检通过")}", "failed" => UiText.IsEnglish(UiText.CurrentLanguage) ? "last self-test failed" : "最近自检失败", _ => null };
                var used = LastUsedAtUnixMs is null ? null : $"{(UiText.IsEnglish(UiText.CurrentLanguage) ? "used " : "使用于 ")}{FormatTimestamp(LastUsedAtUnixMs.Value)}";
                var rights = RightsConfirmedAtUnixMs is null
                    ? UiText.IsEnglish(UiText.CurrentLanguage) ? "rights not confirmed" : "授权未确认"
                    : UiText.IsEnglish(UiText.CurrentLanguage) ? "rights confirmed" : "授权已确认";
                var parts = new[] { profile, rate, UsesF0 is null ? null : UsesF0.Value ? "F0" : UiText.IsEnglish(UiText.CurrentLanguage) ? "non-F0" : "非 F0", SpeakerCount is null ? null : $"{SpeakerCount} speakers", rights, test, used }
                    .Where(value => value is not null);
                return string.Join(" · ", parts);
            }
        }
        public static ModelItem FromJson(JsonElement value) => new(
            value.GetProperty("id").GetString() ?? "",
            value.GetProperty("displayName").GetString() ?? "未命名",
            value.GetProperty("format").GetString() ?? "",
            value.GetProperty("state").GetString() ?? "",
            value.GetProperty("sizeBytes").GetInt64(),
            value.GetProperty("sha256").GetString() ?? "",
            OptionalString(value, "author"), OptionalString(value, "license"),
            value.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Select(tag => tag.GetString() ?? "").Where(tag => tag.Length > 0).ToArray() : [],
            OptionalString(value, "rvcVersion"), OptionalInt(value, "sampleRate"), OptionalBool(value, "usesF0"),
            OptionalInt(value, "speakerCount"), OptionalString(value, "recommendedProvider"),
            OptionalString(value, "testStatus"), OptionalLong(value, "lastTestedAtUnixMs"), OptionalLong(value, "lastUsedAtUnixMs"),
            OptionalLong(value, "rightsConfirmedAtUnixMs"));
        private static string? OptionalString(JsonElement value, string name) =>
            value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        private static int? OptionalInt(JsonElement value, string name) =>
            value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetInt32() : null;
        private static long? OptionalLong(JsonElement value, string name) =>
            value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetInt64() : null;
        private static bool? OptionalBool(JsonElement value, string name) =>
            value.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : null;
        private static string ProviderLabel(string? provider) => provider switch { "nvtrtx" => "TensorRT RTX ", "directml" => "DirectML ", "cpu" => "CPU ", _ => "" };
        private static string FormatTimestamp(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime.ToString("MM-dd HH:mm");
    }

    private sealed record HuggingFaceFileItem(string Path, long SizeBytes, string DownloadUrl)
    {
        public string DisplayLabel => $"{Path}    ({SizeBytes / 1024d / 1024d:N1} MB)";
        public static HuggingFaceFileItem FromJson(JsonElement value) => new(
            value.GetProperty("path").GetString() ?? "",
            value.GetProperty("sizeBytes").GetInt64(),
            value.GetProperty("downloadUrl").GetString() ?? "");
    }

    private sealed record HuggingFaceRepositoryInfo(
        string Repository, string Revision, string? License, bool Gated, bool Private)
    {
        public static HuggingFaceRepositoryInfo FromJson(JsonElement value) => new(
            value.GetProperty("repository").GetString() ?? "未知仓库",
            value.GetProperty("revision").GetString() ?? "main",
            value.TryGetProperty("license", out var license) && license.ValueKind == JsonValueKind.String
                ? license.GetString() : null,
            value.GetProperty("gated").GetBoolean(),
            value.GetProperty("private").GetBoolean());
    }

    private sealed record EngineSnapshot(
        string? State,
        string? Message,
        bool Passthrough,
        int OutputSampleRate,
        ulong ProcessingUs,
        int ChunkMs,
        ulong OutputUnderruns,
        ulong InputOverruns,
        ulong OutputDroppedSamples)
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
            var overruns = root.TryGetProperty("inputOverruns", out var overrunValue) ? overrunValue.GetUInt64() : 0;
            var dropped = root.TryGetProperty("outputDroppedSamples", out var droppedValue) ? droppedValue.GetUInt64() : 0;
            var passthrough = eventName == "audioStarted"
                || (root.TryGetProperty("passthrough", out var passthroughValue) && passthroughValue.GetBoolean())
                || (root.TryGetProperty("mode", out var modeValue) && modeValue.GetString() == "safeBypass");
            return new(state, message, passthrough, sampleRate, processing, chunkMs, underruns, overruns, dropped);
        }
    }
}
