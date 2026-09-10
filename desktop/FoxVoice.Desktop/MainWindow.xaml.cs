using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace FoxVoice.Desktop;

public partial class MainWindow : Window
{
    private Process? _audioProcess;
    private readonly string _supervisorPath;

    public MainWindow()
    {
        InitializeComponent();
        _supervisorPath = FindSupervisor();
        Loaded += async (_, _) => await RefreshAllAsync();
        Closing += (_, _) => StopAudioProcess();
    }

    private async Task RefreshAllAsync()
    {
        try
        {
            BackendStatus.Text = "原生引擎已连接";
            await Task.WhenAll(RefreshDevicesAsync(), RefreshModelsAsync(), RefreshDoctorAsync());
            FooterStatus.Text = "系统状态已刷新";
        }
        catch (Exception error)
        {
            BackendStatus.Text = "原生引擎不可用";
            FooterStatus.Text = error.Message;
        }
    }

    private async Task RefreshDevicesAsync()
    {
        using var document = JsonDocument.Parse(await RunAsync("audio-devices"));
        var devices = document.RootElement.EnumerateArray().Select(AudioDevice.FromJson).ToList();
        InputDevices.ItemsSource = devices.Where(device => device.Direction == "input").ToList();
        OutputDevices.ItemsSource = devices.Where(device => device.Direction == "output").ToList();
        InputDevices.SelectedItem = devices.FirstOrDefault(device => device.Direction == "input" && device.IsDefault);
        OutputDevices.SelectedItem = devices.FirstOrDefault(device => device.Direction == "output" && device.IsDefault);
    }

    private async Task RefreshModelsAsync()
    {
        using var document = JsonDocument.Parse(await RunAsync("models", "list"));
        ModelsGrid.ItemsSource = document.RootElement.EnumerateArray().Select(ModelItem.FromJson).ToList();
    }

    private async Task RefreshDoctorAsync()
    {
        DoctorText.Text = await RunAsync("doctor");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        try { await RefreshModelsAsync(); FooterStatus.Text = "模型库已刷新"; }
        catch (Exception error) { FooterStatus.Text = error.Message; }
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
        try
        {
            FooterStatus.Text = "正在校验并导入模型…";
            await RunAsync("models", "import", dialog.FileName);
            await RefreshModelsAsync();
            FooterStatus.Text = "模型导入完成";
        }
        catch (Exception error) { FooterStatus.Text = error.Message; }
    }

    private void StartBypass_Click(object sender, RoutedEventArgs e)
    {
        if (_audioProcess is { HasExited: false }) return;
        var startInfo = CreateStartInfo("bypass-run");
        _audioProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _audioProcess.OutputDataReceived += AudioOutputReceived;
        _audioProcess.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) Dispatcher.Invoke(() => FooterStatus.Text = args.Data);
        };
        _audioProcess.Exited += (_, _) => Dispatcher.Invoke(() => SetAudioRunning(false));
        _audioProcess.Start();
        _audioProcess.BeginOutputReadLine();
        _audioProcess.BeginErrorReadLine();
        SetAudioRunning(true);
    }

    private void StopBypass_Click(object sender, RoutedEventArgs e) => StopAudioProcess();

    private void AudioOutputReceived(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        try
        {
            using var document = JsonDocument.Parse(args.Data);
            var root = document.RootElement;
            Dispatcher.Invoke(() =>
            {
                if (root.GetProperty("event").GetString() == "audioStarted")
                {
                    AudioStatus.Text = "安全旁路运行中";
                    SampleRateText.Text = $"{root.GetProperty("sampleRate").GetInt32() / 1000} kHz";
                }
                else
                {
                    UnderrunText.Text = root.GetProperty("outputUnderruns").GetUInt64().ToString("N0");
                    StreamErrorText.Text = root.GetProperty("streamErrors").GetUInt64().ToString("N0");
                }
            });
        }
        catch (JsonException) { }
    }

    private void StopAudioProcess()
    {
        if (_audioProcess is { HasExited: false }) _audioProcess.Kill(entireProcessTree: true);
        _audioProcess?.Dispose();
        _audioProcess = null;
        SetAudioRunning(false);
    }

    private void SetAudioRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        AudioStatus.Text = running ? "正在启动…" : "已停止";
        if (!running) SampleRateText.Text = "—";
    }

    private async Task<string> RunAsync(params string[] arguments)
    {
        using var process = new Process { StartInfo = CreateStartInfo(arguments) };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        return output;
    }

    private ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo(_supervisorPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static string FindSupervisor()
    {
        var configured = Environment.GetEnvironmentVariable("FOXVOICE_SUPERVISOR");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var bundled = Path.Combine(AppContext.BaseDirectory, "foxvoice-supervisor.exe");
        if (File.Exists(bundled)) return bundled;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var profile in new[] { "release", "debug" })
            {
                var candidate = Path.Combine(directory.FullName, "native", "target", profile, "foxvoice-supervisor.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("找不到 foxvoice-supervisor.exe，请先运行发布脚本。");
    }

    private sealed record AudioDevice(string Name, string Direction, bool IsDefault)
    {
        public static AudioDevice FromJson(JsonElement value) => new(
            value.GetProperty("name").GetString() ?? "未知设备",
            value.GetProperty("direction").GetString() ?? "",
            value.GetProperty("isDefault").GetBoolean());
    }

    private sealed record ModelItem(string DisplayName, string Format, string State, long SizeBytes, string Hash)
    {
        public string SizeText => $"{SizeBytes / 1024d / 1024d:N1} MB";
        public string ShortHash => Hash.Length > 16 ? Hash[..16] : Hash;
        public static ModelItem FromJson(JsonElement value) => new(
            value.GetProperty("displayName").GetString() ?? "未命名",
            value.GetProperty("format").GetString() ?? "",
            value.GetProperty("state").GetString() ?? "",
            value.GetProperty("sizeBytes").GetInt64(),
            value.GetProperty("sha256").GetString() ?? "");
    }
}
