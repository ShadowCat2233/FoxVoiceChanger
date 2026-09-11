using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FoxVoice.Desktop;

internal static class UiText
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";
    public static string CurrentLanguage { get; set; } = Chinese;

    private static readonly Dictionary<string, string> EnglishText = new(StringComparer.Ordinal)
    {
        ["工作台"] = "WORKSPACE", ["实时变声"] = "Live Voice", ["模型库"] = "Model Library",
        ["音效板"] = "Soundboard", ["模型训练"] = "Model Training", ["组件中心"] = "Component Center",
        ["设置与诊断"] = "Settings & Diagnostics", ["本地模式"] = "Local Mode", ["音频不会上传"] = "Audio never leaves this PC",
        ["让声音保持在游戏里"] = "Keep your voice in the game", ["设备"] = "Devices", ["🎧  设备"] = "🎧  Devices",
        ["引擎"] = "ENGINE", ["正在检测"] = "Detecting", ["正在检测…"] = "Detecting…", ["处理耗时"] = "PROCESSING",
        ["输入"] = "INPUT", ["输出"] = "OUTPUT", ["当前模型"] = "CURRENT MODEL", ["尚未选择"] = "Not selected",
        ["开始变声"] = "Start Voice", ["停止变声"] = "Stop Voice", ["参数调整会实时传给独立推理进程"] = "Changes are sent live to the isolated inference process",
        ["游戏保护"] = "Game Guard", ["资源保护已启用"] = "Resource protection enabled", ["资源保护已关闭"] = "Resource protection disabled",
        ["高优先级音频 · 静态低开销界面"] = "High-priority audio · low-overhead UI", ["处理预算"] = "PROCESSING BUDGET",
        ["快速调音"] = "Quick Tune", ["音高"] = "Pitch", ["输出增益"] = "Output Gain", ["轻量噪声门"] = "Lightweight Noise Gate",
        ["低开销 · 实时参数"] = "Low overhead · live parameters", ["监听自己的声音"] = "Monitor My Voice",
        ["输出到物理设备时谨慎开启"] = "Use carefully with speakers", ["声音预设"] = "Voice Presets", ["查看全部"] = "View All",
        ["添加模型"] = "Add Model", ["本地或 Hugging Face"] = "Local or Hugging Face", ["刷新"] = "Refresh",
        ["＋ 导入本地模型"] = "＋ Import Local Model", ["下载并导入"] = "Download & Import", ["格式"] = "FORMAT",
        ["状态"] = "STATUS", ["大小"] = "SIZE", ["操作"] = "ACTION", ["模型"] = "MODEL", ["使用"] = "Use",
        ["还没有模型"] = "No models yet", ["导入通过结构校验的 RVC ONNX 模型后即可使用"] = "Import a structurally verified RVC ONNX model to begin",
        ["＋ 添加音效"] = "＋ Add Sound", ["共享输出混音"] = "Shared Output Mix", ["音效增益"] = "Sound Gain",
        ["播放"] = "Play", ["▶ 播放"] = "▶ Play", ["移除"] = "Remove", ["循环"] = "Loop", ["全部"] = "All",
        ["常用"] = "Common", ["语音"] = "Voice", ["音乐"] = "Music", ["其他"] = "Other",
        ["隔离训练组件"] = "Isolated Training Components", ["应用内一键训练"] = "One-click Training",
        ["安装 NVIDIA CUDA 训练环境"] = "Install NVIDIA CUDA Training", ["安装 CPU 训练环境"] = "Install CPU Training",
        ["取消安装"] = "Cancel Install", ["选择数据集"] = "Choose Dataset", ["名称"] = "Name", ["轮数"] = "Epochs",
        ["批大小"] = "Batch Size", ["开始一键训练"] = "Start Training", ["打开训练工作台"] = "Open Training Workbench",
        ["导入训练结果"] = "Import Training Result", ["重新检测"] = "Detect Again", ["当前推荐方案"] = "RECOMMENDED",
        ["正在检测硬件…"] = "Detecting hardware…", ["自检中"] = "Testing", ["随桌面程序内置，独立进程运行"] = "Bundled and isolated in a separate process",
        ["已内置"] = "Bundled", ["未安装"] = "Not installed", ["虚拟音频设备"] = "Virtual Audio Device",
        ["检测 VB-CABLE 或兼容虚拟输出"] = "Detect VB-CABLE or a compatible virtual output", ["检测中"] = "Detecting",
        ["刷新全部"] = "Refresh All", ["RVC 基础模型"] = "RVC Foundation Models", ["选择…"] = "Browse…",
        ["运行安全旁路测试"] = "Run Safe Bypass Test", ["实时指标"] = "Live Metrics", ["音频状态"] = "Audio Status",
        ["已停止"] = "Stopped", ["采样率"] = "Sample Rate", ["输出欠载"] = "Output Underruns", ["流错误"] = "Stream Errors",
        ["诊断报告"] = "Diagnostic Report", ["音频设备"] = "Audio Devices", ["麦克风输入"] = "Microphone Input",
        ["监听 / 虚拟声卡输出"] = "Monitor / Virtual Cable Output", ["取消"] = "Cancel", ["保存设备"] = "Save Devices",
        ["引擎待机"] = "Engine idle", ["正在初始化…"] = "Initializing…", ["本地监听设备（可选）"] = "Local monitor device (optional)",
        ["安装基础模型"] = "Install Foundation Models", ["运行 RVC 三模型自检"] = "Run RVC Three-model Test",
        ["安装并自检"] = "Install & Test", ["重新自检"] = "Test Again", ["重新校验"] = "Verify Again",
        ["移入回收区"] = "Move to Recycle Bin", ["编辑资料"] = "Edit Metadata", ["离线编辑"] = "Offline Editor",
        ["取消下载"] = "Cancel Download", ["官方安装说明"] = "Official Setup Guide",
        ["管理本地声音模型"] = "Manage local voice models", ["把音效安全地混入语音"] = "Mix sounds safely into your voice",
        ["本地训练与任务调度"] = "Local training and job scheduling", ["硬件、引擎与音频路由"] = "Hardware, engine, and audio routing",
        ["配置 RVC 基础模型并查看诊断"] = "Configure RVC foundation models and diagnostics",
        ["ContentVec 与 RMVPE 必须是你合法取得的 ONNX 文件。"] = "ContentVec and RMVPE must be ONNX files you obtained legally.",
        ["若要把变声送入游戏，请选择 VB-CABLE Input 等虚拟输出；监听自己的声音时请避免扬声器回授。"] = "Choose a virtual output such as VB-CABLE Input to send the converted voice to a game. Avoid speaker feedback while monitoring.",
        ["音效发送到当前主输出；选择 VB-CABLE 时会与变声一起进入游戏"] = "Sounds are mixed into the main output; with VB-CABLE they enter the game together with the converted voice",
        ["还没有音效。添加 WAV、FLAC、MP3 或 OGG 后，可发送到当前主输出。"] = "No sounds yet. Add WAV, FLAC, MP3, or OGG files to play them through the main output.",
        ["固定官方 RVC 源代码版本；Python 3.12、FFmpeg、PyTorch 和训练权重按需安装到 LocalAppData，不进入实时引擎。"] = "A pinned official RVC source version; Python 3.12, FFmpeg, PyTorch, and training weights install on demand outside the real-time engine.",
        ["数据集目录（本人授权的干净人声 WAV/FLAC）"] = "Dataset folder (authorized clean voice WAV/FLAC)",
        ["编辑模型资料"] = "Edit Model Metadata", ["模型资料"] = "Model Metadata", ["保存"] = "Save",
        ["作者（可选）"] = "Author (optional)", ["许可证（可选）"] = "License (optional)",
        ["标签（英文逗号分隔，最多 20 个）"] = "Tags (comma-separated, up to 20)",
        ["资料只保存在本机 model.json；许可证应以模型发布者声明为准。"] = "Metadata is stored only in local model.json; the publisher's license declaration remains authoritative.",
        ["模型名称不能为空。"] = "Model name cannot be empty.", ["离线音频裁剪"] = "Offline Audio Trim",
        ["选择需要变声的单轨区间"] = "Select the portion of this track to convert", ["开始位置"] = "Start Position",
        ["结束位置"] = "End Position", ["试听所选原声"] = "Preview Original Selection", ["转换所选区间"] = "Convert Selection",
        ["安装 RVC 基础模型"] = "Install RVC Foundation Models", ["安装 TensorRT RTX"] = "Install TensorRT RTX",
        ["安装训练组件"] = "Install Training Components", ["开始一键训练"] = "Start One-click Training",
        ["移除模型"] = "Remove Model", ["确认模型来源"] = "Confirm Model Source", ["离线转换"] = "Offline Conversion",
        ["选择待转换音频"] = "Choose Audio to Convert", ["保存变声音频"] = "Save Converted Audio",
        ["添加音效"] = "Add Sound", ["选择 ContentVec ONNX"] = "Choose ContentVec ONNX", ["选择 RMVPE ONNX"] = "Choose RMVPE ONNX",
        ["导入 RVC 模型或索引"] = "Import RVC Model or Index", ["选择 Hugging Face 模型文件"] = "Choose Hugging Face Model File",
        ["选择已获授权的训练音频目录"] = "Choose an Authorized Training Audio Folder",
        ["训练会长时间占用 CPU/GPU。请确认数据集中的声音均已获得授权，训练期间不要启动游戏。"] = "Training uses CPU/GPU for a long time. Confirm every voice in the dataset is authorized and do not start a game during training.",
        ["转换完成。是否立即试听结果？"] = "Conversion completed. Preview the result now?",
        ["安全旁路运行中"] = "Safe bypass running", ["实时变声运行中"] = "Live voice running",
        ["实时引擎运行中"] = "Real-time engine running", ["音频链路正常"] = "Audio path healthy",
        ["引擎启动中"] = "Engine starting", ["正在启动…"] = "Starting…", ["DirectML 已就绪"] = "DirectML ready",
        ["引擎连接失败"] = "Engine connection failed", ["资源保护已启用，等待游戏或实时引擎"] = "Resource protection enabled; waiting for a game or the real-time engine",
        ["稳定档 · 保持当前链路"] = "Stable · current pipeline retained", ["保生存档 · 无缝原声旁路"] = "Survival · seamless original-voice bypass",
        ["保护旁路 · 等待恢复"] = "Protective bypass · waiting to recover", ["本机组件、设备与模型库已就绪"] = "Local components, devices, and model library are ready",
        ["FOXVOICE / 实时变声"] = "FOXVOICE / LIVE VOICE", ["FOXVOICE / 模型库"] = "FOXVOICE / MODEL LIBRARY",
        ["FOXVOICE / 音效板"] = "FOXVOICE / SOUNDBOARD", ["FOXVOICE / 模型训练"] = "FOXVOICE / MODEL TRAINING",
        ["FOXVOICE / 组件中心"] = "FOXVOICE / COMPONENT CENTER", ["FOXVOICE / 设置"] = "FOXVOICE / SETTINGS"
    };

    private static readonly DependencyProperty OriginalTextProperty = DependencyProperty.RegisterAttached(
        "OriginalText", typeof(string), typeof(UiText), new PropertyMetadata(null));
    private static readonly DependencyProperty OriginalContentProperty = DependencyProperty.RegisterAttached(
        "OriginalContent", typeof(string), typeof(UiText), new PropertyMetadata(null));
    private static readonly DependencyProperty OriginalToolTipProperty = DependencyProperty.RegisterAttached(
        "OriginalToolTip", typeof(string), typeof(UiText), new PropertyMetadata(null));

    public static bool IsEnglish(string? language) => string.Equals(language, English, StringComparison.OrdinalIgnoreCase);

    public static string Translate(string value, string? language)
    {
        if (!IsEnglish(language) || string.IsNullOrEmpty(value)) return value;
        if (EnglishText.TryGetValue(value, out var exact)) return exact;

        return value
            .Replace("正在检测", "Detecting", StringComparison.Ordinal)
            .Replace("未检测到", "Not detected", StringComparison.Ordinal)
            .Replace("已检测到", "Detected", StringComparison.Ordinal)
            .Replace("已安装", "Installed", StringComparison.Ordinal)
            .Replace("未安装", "Not installed", StringComparison.Ordinal)
            .Replace("自检通过", "Self-test passed", StringComparison.Ordinal)
            .Replace("不可用", "Unavailable", StringComparison.Ordinal)
            .Replace("未知设备", "Unknown device", StringComparison.Ordinal)
            .Replace("没有输入设备", "No input device", StringComparison.Ordinal)
            .Replace("没有输出设备", "No output device", StringComparison.Ordinal);
    }

    public static string Translate(string value) => Translate(value, CurrentLanguage);

    public static void Apply(DependencyObject root, string? language)
    {
        CurrentLanguage = IsEnglish(language) ? English : Chinese;
        ApplyOne(root, language);
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            Apply(System.Windows.Media.VisualTreeHelper.GetChild(root, i), language);
    }

    private static void ApplyOne(DependencyObject element, string? language)
    {
        if (element is TextBlock text && !BindingOperations.IsDataBound(text, TextBlock.TextProperty))
            text.Text = LocalizedValue(text, OriginalTextProperty, text.Text, language);
        if (element is ContentControl content && content.Content is string value &&
            !BindingOperations.IsDataBound(content, ContentControl.ContentProperty))
            content.Content = LocalizedValue(content, OriginalContentProperty, value, language);
        if (element is FrameworkElement framework && framework.ToolTip is string tooltip)
            framework.ToolTip = LocalizedValue(framework, OriginalToolTipProperty, tooltip, language);
    }

    private static string LocalizedValue(
        DependencyObject element,
        DependencyProperty originalProperty,
        string current,
        string? language)
    {
        var original = (string?)element.GetValue(originalProperty);
        if (original is null || (current != original && current != Translate(original, English)))
        {
            original = current;
            element.SetValue(originalProperty, original);
        }
        return Translate(original, language);
    }
}
