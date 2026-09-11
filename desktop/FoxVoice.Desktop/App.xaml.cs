using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace FoxVoice.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = WriteCrashLog(e.Exception);
        var english = UiText.IsEnglish(UserSettings.Load().Language);
        MessageBox.Show(
            english
                ? $"FoxVoice encountered an error and prevented an abrupt exit.\n\n{e.Exception.Message}\n\nDiagnostic log: {logPath}"
                : $"FoxVoice 遇到错误，但已阻止程序直接闪退。\n\n{e.Exception.Message}\n\n诊断日志：{logPath}",
            english ? "FoxVoice Error" : "FoxVoice 错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static string WriteCrashLog(Exception error)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FoxVoice",
                "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "desktop-crash.log");
            var entry = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}]")
                .AppendLine(error.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(path, entry, Encoding.UTF8);
            return path;
        }
        catch
        {
            return UiText.IsEnglish(UserSettings.Load().Language) ? "Failed to write log" : "日志写入失败";
        }
    }
}
