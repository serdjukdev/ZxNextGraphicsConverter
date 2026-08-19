using System.Text.Json;

namespace ZxNext.Core.Project;

/// <summary>Tracks the last 10 opened/saved project folder paths, most-recent-first, in a small per-user settings file.</summary>
public static class RecentProjectsStore
{
    private const int MaxEntries = 10;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZxNextGraphicsConverter",
        "recent-projects.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
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

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
    }
}
