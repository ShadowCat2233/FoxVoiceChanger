using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace FoxVoice.Setup;

internal static class Program
{
    private const string PayloadResource = "FoxVoice.Payload.zip";
    private static readonly string ProductVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    private static readonly string? TestRoot = Environment.GetEnvironmentVariable("FOXVOICE_SETUP_TEST_ROOT");
    private static readonly string InstallRoot = TestRoot is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FoxVoice")
        : Path.Combine(Path.GetFullPath(TestRoot), "Programs", "FoxVoice");
    private static readonly string CurrentDirectory = Path.Combine(InstallRoot, "current");
    private static readonly string RollbackDirectory = Path.Combine(InstallRoot, "rollback");
    private static readonly string InstallerRoot = TestRoot is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxVoice", "installer")
        : Path.Combine(Path.GetFullPath(TestRoot), "Data", "installer");
    private static readonly string CachedInstaller = Path.Combine(InstallerRoot, "FoxVoiceSetup.exe");

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var action = args.FirstOrDefault()?.TrimStart('-', '/').ToLowerInvariant() ?? "install";
            var yes = args.Any(value => value.Equals("--yes", StringComparison.OrdinalIgnoreCase));
            return action switch
            {
                "install" or "upgrade" or "repair" => InstallOrRepair(action, yes),
                "rollback" => Rollback(yes),
                "uninstall" => Uninstall(yes),
                "status" => ShowStatus(),
                "verify" => VerifyPayload(),
                _ => throw new InvalidOperationException("用法：FoxVoiceSetup.exe [install|repair|rollback|uninstall|status|verify] [--yes]")
            };
        }
        catch (Exception error)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"FoxVoice 安装失败：{error.Message}");
            Console.ResetColor();
            PauseIfInteractive(args);
            return 1;
        }
    }

    private static int InstallOrRepair(string action, bool yes)
    {
        var label = Directory.Exists(CurrentDirectory) ? (action == "repair" ? "修复" : "升级") : "安装";
        if (!Confirm($"将为当前 Windows 用户{label} FoxVoice，不需要管理员权限。模型、训练数据和设置不会被覆盖。", yes)) return 2;
        Directory.CreateDirectory(InstallRoot);
        var staging = Path.Combine(InstallRoot, $".staging-{Environment.ProcessId}-{Guid.NewGuid():N}");
        ExtractAndVerifyPayload(staging);
        ReplaceCurrent(staging);
        CacheInstaller();
        CreateShortcuts();
        RegisterUninstall();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"FoxVoice {label}完成：{Path.Combine(CurrentDirectory, "FoxVoice.exe")}");
        Console.ResetColor();
        PauseIfInteractive(Environment.GetCommandLineArgs().Skip(1).ToArray());
        return 0;
    }

    private static int Rollback(bool yes)
    {
        EnsureDirectory(RollbackDirectory, "没有可回滚的上一版本。");
        if (!Confirm("将当前 FoxVoice 与上一版本互换。模型和设置不会改变。", yes)) return 2;
        var swap = Path.Combine(InstallRoot, $".swap-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            if (Directory.Exists(CurrentDirectory)) Directory.Move(CurrentDirectory, swap);
            Directory.Move(RollbackDirectory, CurrentDirectory);
            if (Directory.Exists(swap)) Directory.Move(swap, RollbackDirectory);
        }
        catch
        {
            if (!Directory.Exists(CurrentDirectory) && Directory.Exists(RollbackDirectory))
                Directory.Move(RollbackDirectory, CurrentDirectory);
            if (!Directory.Exists(RollbackDirectory) && Directory.Exists(swap))
                Directory.Move(swap, RollbackDirectory);
            throw;
        }
        CreateShortcuts();
        Console.WriteLine("已回滚；刚才的版本保留为下一次可切换版本。");
        return 0;
    }

    private static int Uninstall(bool yes)
    {
        if (!Confirm("将卸载 FoxVoice 程序文件。LocalAppData\\FoxVoice 下的模型、设置、组件和训练数据将保留。", yes)) return 2;
        DeleteDirectory(CurrentDirectory);
        DeleteDirectory(RollbackDirectory);
        if (TestRoot is null)
        {
            DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FoxVoice.lnk"));
            DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "FoxVoice.lnk"));
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FoxVoice", false);
        }
        Console.WriteLine("FoxVoice 程序已卸载；用户模型和设置已保留。");
        return 0;
    }

    private static int ShowStatus()
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            installed = File.Exists(Path.Combine(CurrentDirectory, "FoxVoice.exe")),
            installerVersion = ProductVersion,
            currentVersion = InstalledVersion(CurrentDirectory),
            rollbackVersion = InstalledVersion(RollbackDirectory),
            rollbackAvailable = File.Exists(Path.Combine(RollbackDirectory, "FoxVoice.exe")),
            installPath = CurrentDirectory,
            userDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxVoice")
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int VerifyPayload()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"FoxVoiceSetupVerify-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            ExtractAndVerifyPayload(temporary);
            Console.WriteLine("FoxVoice 安装负载及全部文件 SHA-256 校验通过。");
            return 0;
        }
        finally { DeleteDirectory(temporary); }
    }

    private static void ExtractAndVerifyPayload(string staging)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("安装器缺少内嵌发布负载。");
        Directory.CreateDirectory(staging);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var stagingRoot = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("发布负载包含不安全路径。");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
        VerifyManifest(staging);
        var desktop = Path.Combine(staging, "FoxVoice.exe");
        if (!File.Exists(desktop)) throw new InvalidDataException("发布负载缺少 FoxVoice.exe。");
        var payloadVersion = FileVersionInfo.GetVersionInfo(desktop).ProductVersion;
        if (!string.Equals(payloadVersion, ProductVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"安装器版本 {ProductVersion} 与发布负载版本 {payloadVersion ?? "未知"} 不一致。");
    }

    private static void VerifyManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, "SHA256SUMS.json");
        var entries = JsonSerializer.Deserialize<List<HashEntry>>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("发布哈希清单无效。");
        foreach (var entry in entries)
        {
            var path = Path.GetFullPath(Path.Combine(directory, entry.File));
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidDataException($"发布文件缺失或路径不安全：{entry.File}");
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"发布文件哈希不匹配：{entry.File}");
        }
    }

    private static void ReplaceCurrent(string staging)
    {
        DeleteDirectory(RollbackDirectory);
        try
        {
            if (Directory.Exists(CurrentDirectory)) Directory.Move(CurrentDirectory, RollbackDirectory);
            Directory.Move(staging, CurrentDirectory);
        }
        catch
        {
            DeleteDirectory(staging);
            if (!Directory.Exists(CurrentDirectory) && Directory.Exists(RollbackDirectory))
                Directory.Move(RollbackDirectory, CurrentDirectory);
            throw;
        }
    }

    private static void CacheInstaller()
    {
        Directory.CreateDirectory(InstallerRoot);
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位安装器自身。");
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(CachedInstaller), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, CachedInstaller, true);
    }

    private static void RegisterUninstall()
    {
        if (TestRoot is not null) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FoxVoice");
        key.SetValue("DisplayName", "FoxVoice 狐声");
        key.SetValue("Publisher", "FoxVoice");
        key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("InstallLocation", CurrentDirectory);
        key.SetValue("DisplayIcon", Path.Combine(CurrentDirectory, "FoxVoice.exe"));
        key.SetValue("UninstallString", $"\"{CachedInstaller}\" uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
    }

    private static string? InstalledVersion(string directory)
    {
        var executable = Path.Combine(directory, "FoxVoice.exe");
        return File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).ProductVersion : null;
    }

    private static void CreateShortcuts()
    {
        if (TestRoot is not null) return;
        var target = Path.Combine(CurrentDirectory, "FoxVoice.exe");
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FoxVoice.lnk"), target);
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "FoxVoice.lnk"), target);
    }

    private static void CreateShortcut(string shortcutPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows 快捷方式服务不可用。");
        var shell = Activator.CreateInstance(shellType)!;
        var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])!;
        var type = shortcut.GetType();
        type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [target]);
        type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [CurrentDirectory]);
        type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["FoxVoice 狐声实时变声器"]);
        type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        if (shortcut is IDisposable disposable) disposable.Dispose();
        if (shell is IDisposable shellDisposable) shellDisposable.Dispose();
    }

    private static bool Confirm(string message, bool yes)
    {
        Console.WriteLine(message);
        if (yes) return true;
        Console.Write("继续？[y/N] ");
        return Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void PauseIfInteractive(string[] args)
    {
        if (args.Any(value => value.Equals("--yes", StringComparison.OrdinalIgnoreCase)) || Console.IsInputRedirected) return;
        Console.WriteLine("按 Enter 关闭…");
        Console.ReadLine();
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static void DeleteShortcut(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void EnsureDirectory(string path, string message)
    {
        if (!Directory.Exists(path)) throw new InvalidOperationException(message);
    }

    private sealed record HashEntry(string File, string Sha256);
}
