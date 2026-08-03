using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace EQ2Parser.App.Services;

/// <summary>
/// Auto-update via Velopack + GitHub Releases: check quietly at startup,
/// download in the background, apply when the app exits — nobody has to
/// chase versions in Discord. Running from source (not installed) turns
/// the whole thing into a no-op with an honest status line.
/// </summary>
public sealed class UpdateService
{
    /// <summary>The PUBLIC releases-only repo — the updater reads it
    /// anonymously, so it must stay public while the code repo is private.
    /// If releases ever move back to the code repo, ship a transitional
    /// build from THIS feed first or existing installs never see the move.</summary>
    public const string RepoUrl = "https://github.com/VortexUK/EQ2Parser-releases";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, prerelease: true));

    /// <summary>Raised (on a background thread) whenever the status line
    /// changes — Settings shows it.</summary>
    public event Action<string>? StatusChanged;

    public string Status { get; private set; } = "";

    public string CurrentVersion =>
        _manager.IsInstalled
            ? _manager.CurrentVersion?.ToString() ?? "?"
            : Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) + " (from source)";

    private void Set(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>Startup check: fire-and-forget; never throws.</summary>
    public async Task CheckAndDownloadAsync()
    {
        if (!_manager.IsInstalled)
        {
            Set("running from source — update via git pull");
            return;
        }
        try
        {
            Set("checking for updates…");
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                Set("up to date");
                return;
            }
            Set($"downloading v{update.TargetFullRelease.Version}…");
            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            // Applies silently after the app closes; next launch is current.
            _manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
            Set($"v{update.TargetFullRelease.Version} downloaded — applies when you close the app");
        }
        catch (Exception ex)
        {
            Set($"update check failed ({ex.Message}) — will retry next launch");
        }
    }
}
