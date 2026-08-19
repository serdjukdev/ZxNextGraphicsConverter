namespace ZxNext.Core.Settings;

/// <summary>Small per-user app preferences that aren't part of any project: remembered folder locations and window geometry.</summary>
public class AppSettings
{
    public string? LastImportDirectory { get; set; }
    public string? LastProjectDirectory { get; set; }
    public string? LastExportDirectory { get; set; }

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
}
