using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace FoxVoice.Desktop;

internal static class NativeBundle
{
    public static string EnsureExtracted(string fileName)
    {
        var resourceName = $"FoxVoice.Native.{fileName}";
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (resource is null) throw new FileNotFoundException($"程序包缺少嵌入组件：{fileName}");

        var configuredRuntime = Environment.GetEnvironmentVariable("FOXVOICE_RUNTIME_DIR");
        var productVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var runtimeDirectory = string.IsNullOrWhiteSpace(configuredRuntime)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FoxVoice", "runtime", productVersion)
            : Path.GetFullPath(configuredRuntime);
        Directory.CreateDirectory(runtimeDirectory);
        var destination = Path.Combine(runtimeDirectory, fileName);
        var expectedHash = Convert.ToHexString(SHA256.HashData(resource));

        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            if (Convert.ToHexString(SHA256.HashData(existing)) == expectedHash) return destination;
        }

        resource.Position = 0;
        var temporary = destination + $".{Environment.ProcessId}.tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            resource.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination, overwrite: true);
        return destination;
    }
}
