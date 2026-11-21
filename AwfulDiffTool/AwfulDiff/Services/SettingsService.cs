using AwfulDiff.Models;
using System.IO;

namespace AwfulDiff.Services;

/// <summary>
/// Loads and saves application settings to %APPDATA%\AwfulDiff\settings.json.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AwfulDiff",
        "settings.json");

    /// <summary>
    /// Load settings from disk, returning defaults if the file is missing or corrupted.
    /// </summary>
    public static AppSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                return AppSettings.FromJson(File.ReadAllText(SettingsPath));
            }
            catch
            {
                return AppSettings.Default;
            }
        }

        return AppSettings.Default;
    }

    /// <summary>
    /// Persist settings to disk. Creates the directory if it doesn't exist.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, settings.ToJson());
        }
        catch
        {
            // Fail silently — settings persistence is non-critical
        }
    }
}
