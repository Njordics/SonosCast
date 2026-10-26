using System.Text.Json;
using Microsoft.Win32;

namespace SonosCast;

/// <summary>User preferences, stored as JSON in %AppData%\SonosCast.</summary>
public sealed class Settings
{
    public string? LastSpeakerUuid { get; set; }
    public bool AutoPlay { get; set; }

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonosCast");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly string LegacySpeakerFile = Path.Combine(Dir, "last-speaker.txt");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            // Carry over the speaker remembered by version 1.0.
            if (File.Exists(LegacySpeakerFile)) return new Settings { LastSpeakerUuid = File.ReadAllText(LegacySpeakerFile).Trim() };
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---------- start with Windows (per-user Run key, no admin needed) ----------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "SonosCast";
    public const string TrayArg = "--tray";

    static string RunCommand => $"\"{Environment.ProcessPath}\" {TrayArg}";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string cmd && cmd.Equals(RunCommand, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, RunCommand);
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
