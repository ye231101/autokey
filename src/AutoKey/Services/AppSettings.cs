using System.Text.Json;
using System.Text.Json.Serialization;
using AutoKey.Platform;

namespace AutoKey.Services;

/// <summary>
/// User preferences, stored in the per-user application data folder (%APPDATA%\AutoKey on Windows,
/// ~/.config/AutoKey on Linux, ~/Library/Application Support/AutoKey on macOS). The text itself is never saved.
/// </summary>
internal sealed class AppSettings
{
    private static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoKey", "settings.json");

    public double WordsPerMinute { get; set; } = 60;

    public double VariationPercent { get; set; } = 50;

    public double TypoPercent { get; set; } = 3;

    public double PausePercent { get; set; } = 100;

    public double StartDelaySeconds { get; set; } = 3;

    public bool PauseOnFocusLoss { get; set; } = true;

    public bool ShowOverlay { get; set; } = true;

    public bool ShiftEnterForNewLines { get; set; }

    public bool KeepCodeEditorTextExact { get; set; } = true;

    public TypingMethod TypingMethod { get; set; } = TypingMethod.Keyboard;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJson.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings fall back to the defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to save preferences isn't worth bothering the user about.
        }
    }
}

/// <summary>JSON code generated at build time, so reading the settings doesn't slow down every start with reflection.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
