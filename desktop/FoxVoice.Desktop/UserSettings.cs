using System.IO;
using System.Text.Json;

namespace FoxVoice.Desktop;

internal sealed class UserSettings
{
    public string EmbedderPath { get; set; } = "";
    public string F0Path { get; set; } = "";
    public string InputDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "";
    public string MonitorOutputDevice { get; set; } = "";
    public string SelectedModelId { get; set; } = "";
    public double Pitch { get; set; }
    public double OutputGainDb { get; set; }
    public bool NoiseGateEnabled { get; set; }
    public bool GameGuardEnabled { get; set; } = true;
    public string PreferredProvider { get; set; } = "directml";
    public List<string> SoundboardFiles { get; set; } = [];
    public Dictionary<string, string> SoundboardGroups { get; set; } = [];
    public List<string> SoundboardLoopFiles { get; set; } = [];
    public double SoundboardGainDb { get; set; } = -3;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FoxVoice", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, overwrite: true);
    }
}
