using System.Runtime.CompilerServices;

namespace ZxNext.Core.Tests;

/// <summary>
/// Redirects AppSettingsStore/RecentProjectsStore to an isolated temp folder for this entire test run,
/// via ZXNEXT_SETTINGS_DIR (see AppSettingsStore.FilePath's doc comment) — runs once, before any test,
/// so no Core test's result can ever depend on whatever the real user has actually saved from the running
/// app on this machine.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var isolatedDir = Path.Combine(Path.GetTempPath(), $"zxnext_test_settings_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("ZXNEXT_SETTINGS_DIR", isolatedDir);
    }
}
