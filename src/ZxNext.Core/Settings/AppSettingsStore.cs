using System.Text.Json;

namespace ZxNext.Core.Settings;

public static class AppSettingsStore
{
    /// <summary>
    /// Real per-user settings live under the OS's ApplicationData folder, but ZxNext.Core.Tests sets the
    /// ZXNEXT_SETTINGS_DIR environment variable (once, at test-assembly startup) to an isolated temp
    /// folder instead: Core code (e.g. ExportFileNaming, which reads overrides via this store) must never
    /// depend on the real machine's actual settings.json, or test results would depend on whatever the
    /// user happens to have saved from the running app — exactly the bug that motivated adding this.
    /// </summary>
    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("ZXNEXT_SETTINGS_DIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZxNextGraphicsConverter",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
