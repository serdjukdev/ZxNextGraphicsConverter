using System.Text.Json;

namespace ZxNext.Core.Project;

/// <summary>Tracks the last 10 opened/saved project folder paths, most-recent-first, in a small per-user settings file.</summary>
public static class RecentProjectsStore
{
    private const int MaxEntries = 10;

    /// <summary>Same ZXNEXT_SETTINGS_DIR test-isolation seam as AppSettingsStore.FilePath — see its doc comment.</summary>
    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("ZXNEXT_SETTINGS_DIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZxNextGraphicsConverter",
        "recent-projects.json");

    /// <summary>Prunes any path whose project file no longer exists (deleted/moved/renamed outside the app) before returning — and persists that pruning, so a dead entry disappears for good after the first load that notices it, not just from this one call's result.</summary>
    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
            var existing = list.Where(File.Exists).ToList();
            if (existing.Count != list.Count) SaveList(existing);
            return existing;
        }
        catch
        {
            return [];
        }
    }

    public static void AddRecent(string projectPath)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p, projectPath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, projectPath);
        if (list.Count > MaxEntries) list = list[..MaxEntries];
        SaveList(list);
    }

    private static void SaveList(List<string> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
    }
}
