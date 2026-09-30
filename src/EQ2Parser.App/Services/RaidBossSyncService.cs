using System.IO;
using System.Net.Http;
using EQ2Parser.Core.Persistence;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.App.Services;

/// <summary>
/// Keeps the site's curated raid-boss name list on hand for the "upload
/// only raid fights" option (GET /api/zones/raid-bosses, no token — public
/// game data). Same shape as the trigger-pack sync: the cached copy
/// (%LocalAppData%\EQ2Parser\raid_bosses.json) applies instantly and
/// offline, a background fetch replaces it when the server's version stamp
/// moves. Nothing here ever throws — a failed fetch leaves the last good
/// list (or none: the filter then decides on headcount alone).
/// </summary>
public sealed class RaidBossSyncService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>How long a successful fetch stays fresh before the tick
    /// re-checks (curator edits are rare; the list is a few KB).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private bool _cacheLoaded;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
    private Task? _inFlight;

    public string BaseUrl { get; }

    /// <summary>Normalised boss names, or null until a cache or fetch has
    /// landed. Replaced wholesale on each apply (readers snapshot the
    /// reference, so no locking on the hot path).</summary>
    public IReadOnlySet<string>? Bosses { get; private set; }

    /// <summary>Version stamp of the applied list ("" = none).</summary>
    public string Version { get; private set; } = "";

    public RaidBossSyncService(string baseUrl) : this(baseUrl, SharedHttp)
    {
    }

    /// <summary>Test seam: inject the transport.</summary>
    internal RaidBossSyncService(string baseUrl, HttpClient http)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = http;
    }

    private static string CachePath => Path.Combine(AppSettings.Directory, "raid_bosses.json");

    /// <summary>Apply the cached list (once) and fetch if the last fetch is
    /// older than <see cref="RefreshInterval"/>. Idempotent and cheap to
    /// call from Configure and from the shell tick; concurrent callers
    /// share one in-flight fetch.</summary>
    public Task EnsureFreshAsync(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_cacheLoaded)
            {
                _cacheLoaded = true;
                LoadCache();
            }
            if (_inFlight is { IsCompleted: false } running)
                return running;
            if (now - _lastFetch < RefreshInterval)
                return Task.CompletedTask;
            _lastFetch = now;
            return _inFlight = SyncAsync();
        }
    }

    private void LoadCache()
    {
        try
        {
            // The cache is the server body verbatim (SaveText below); a
            // malformed or stale-shaped file parses to null and is simply
            // ignored — the fetch replaces it. Never blocks startup.
            if (File.Exists(CachePath))
                Apply(RaidBossPack.Parse(File.ReadAllText(CachePath)));
        }
        catch (Exception)
        {
            // Unreadable cache — the fetch replaces it.
        }
    }

    /// <summary>Fetch the pack and apply it if the version moved. Never throws.</summary>
    public async Task SyncAsync()
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/api/zones/raid-bosses").ConfigureAwait(false);
            var pack = RaidBossPack.Parse(json);
            if (pack is null)
                return;
            if (Version.Length > 0 && pack.Version == Version)
                return;
            PersistedJsonFile.SaveText(CachePath, json);
            Apply(pack);
        }
        catch (Exception)
        {
            // Offline / server trouble: keep the last good list.
        }
    }

    private void Apply(RaidBossPack? pack)
    {
        if (pack is null)
            return;
        Bosses = pack.ToSet();
        Version = pack.Version ?? "";
    }
}
