using Velopack;
using Velopack.Sources;

namespace MixDeck;

/// <summary>
/// Checks for and installs new versions published as GitHub Releases (or a local folder of releases),
/// built with build\release.ps1. Only works when MixDeck was installed with Setup.exe.
/// </summary>
public sealed class Updater
{
    UpdateInfo? _pending;

    public static string CurrentVersion =>
        typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "?";

    public bool IsInstalled => Manager(null)?.IsInstalled == true;
    public string? AvailableVersion => _pending?.TargetFullRelease.Version.ToString();

    static UpdateManager? Manager(string? source)
    {
        try
        {
            IUpdateSource src = string.IsNullOrWhiteSpace(source)
                ? new SimpleFileSource(new DirectoryInfo(AppContext.BaseDirectory))
                : source.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                    ? new GithubSource(source.Trim().TrimEnd('/'), null, false)
                    : source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? new SimpleWebSource(source.Trim())
                        : new SimpleFileSource(new DirectoryInfo(source.Trim()));
            return new UpdateManager(src);
        }
        catch (Exception ex)
        {
            Log.Write($"Update source invalid: {ex.Message}");
            return null;
        }
    }

    /// <returns>A short status line for the settings page.</returns>
    public async Task<string> CheckAsync(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "Enter the repository that updates should come from.";
        var mgr = Manager(source);
        if (mgr == null) return "Invalid update source.";
        if (!mgr.IsInstalled) return "Updates work once MixDeck is installed with Setup.exe (this copy runs straight from a folder).";
        try
        {
            _pending = await mgr.CheckForUpdatesAsync();
            return _pending == null
                ? $"You have the latest version ({CurrentVersion})."
                : $"Version {AvailableVersion} is available.";
        }
        catch (Exception ex)
        {
            Log.Write($"Update check failed: {ex.Message}");
            return "Couldn't check for updates (no internet or wrong address).";
        }
    }

    /// <summary>Downloads the pending update, runs <paramref name="beforeRestart"/> (clean shutdown) and restarts into it.</summary>
    public async Task InstallAsync(string? source, Action beforeRestart)
    {
        var mgr = Manager(source);
        if (mgr == null || _pending == null) return;
        await mgr.DownloadUpdatesAsync(_pending);
        beforeRestart();
        mgr.ApplyUpdatesAndRestart(_pending);
    }
}
