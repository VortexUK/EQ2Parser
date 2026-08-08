using EQ2Parser.App.Localization;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.App.Services;

/// <summary>
/// Wires finished encounters into the EQ2Lexicon upload queue. The engine's
/// EncounterEnded fires under the global sync lock, so the handler only
/// enqueues — payload building and HTTP happen on the queue's background
/// drain. Configure runs at startup and whenever the Settings card changes
/// the token, URL, or toggle.
/// </summary>
public sealed class UploadService : IDisposable
{
    private readonly UploadQueue _queue;
    private LexiconUploadClient? _client;
    private volatile bool _enabled;

    public UploadService()
    {
        _queue = new UploadQueue(SendAsync, ProbeProvenance);
        _queue.StatusChanged += () => Set(_queue.Status);
    }

    /// <summary>Log-writer provenance for an auto upload: who holds the log
    /// file right now (seconds after the fight ended)? EverQuest2 among the
    /// holders → the positive live-log stamp; see LogProvenance.</summary>
    private static List<string>? ProbeProvenance(Encounter encounter) =>
        OperatingSystem.IsWindows()
            ? LogProvenance.BuildWarnings(
                Core.Logs.LogFileHolders.Probe(encounter.SourceId), Environment.ProcessId)
            : null;

    /// <summary>Last event line — the Settings card shows it.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Raised (on a background thread) whenever Status changes.</summary>
    public event Action? StatusChanged;

    /// <summary>A usable client exists (token saved + acceptable URL).</summary>
    public bool Configured => _client is not null;

    /// <summary>Auto-upload is switched on AND configured.</summary>
    public bool Active => _enabled && Configured;

    private void Set(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    private Task<UploadResult> SendAsync(LexiconPayload payload, CancellationToken ct) =>
        _client is { } client
            ? client.UploadAsync(payload, ct)
            : Task.FromResult(new UploadResult(false, 0, Loc.Get("UploadSvc_NoTokenConfigured")));

    public void Configure(string baseUrl, string? apiToken, bool enabled)
    {
        _enabled = enabled;
        // The old client is dropped, not disposed — an in-flight send may
        // still hold it, and the queue treats a disposed-client throw as a
        // plain failure anyway. Reconfigures are rare; the GC collects it.
        _client = null;
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            Set(enabled ? Loc.Get("UploadSvc_NoTokenSaveBelow") : "");
            return;
        }
        if (LexiconUploadClient.UrlProblem(baseUrl) is { } problem)
        {
            Set(problem);
            return;
        }
        _client = new LexiconUploadClient(baseUrl, apiToken);
        _queue.ResetAuthPause();
        if (enabled)
            Set(Loc.Get("UploadSvc_Ready"));
    }

    /// <summary>Engine EncounterEnded handler — never blocks (pump thread,
    /// inside the global sync lock).</summary>
    public void OnEncounterEnded(Encounter encounter)
    {
        if (!Active)
            return;
        _queue.Enqueue(encounter, LogPaths.ParseServerName(encounter.SourceId));
    }

    /// <summary>Fight-tree manual upload: every source's view of the fight,
    /// exactly what auto-upload would have sent (the site mirror-groups them
    /// and keeps the longest as primary). Works with the auto toggle off —
    /// right-clicking Upload IS the consent — and clears a bad-token pause,
    /// since the explicit action is the retry.</summary>
    public void UploadFight(IReadOnlyList<Encounter> sources)
    {
        if (!Configured)
        {
            Set(Loc.Get("UploadSvc_AddTokenInSettings"));
            return;
        }
        _queue.ResetAuthPause();
        // No provenance on manual uploads: the fight may have ended long
        // ago, and probing the log NOW says nothing about who wrote it THEN.
        foreach (var encounter in sources)
            _queue.Enqueue(encounter, LogPaths.ParseServerName(encounter.SourceId), withProvenance: false);
    }

    /// <summary>Settings "Test" button — verifies the token against
    /// /api/auth/whoami and reports who it belongs to.</summary>
    public async Task<string> TestTokenAsync()
    {
        if (_client is not { } client)
            return Loc.Get("UploadSvc_NoTokenSaved");
        var result = await client.WhoAmIAsync().ConfigureAwait(false);
        if (!result.Success)
            return result.StatusCode == 0
                ? result.Message
                : Loc.Format("UploadSvc_TokenRejected", result.StatusCode, result.Message);
        _queue.ResetAuthPause();
        var name = ExtractDiscordName(result.Body);
        return name is null ? Loc.Get("UploadSvc_TokenOk") : Loc.Format("UploadSvc_TokenOkSignedIn", name);
    }

    private static string? ExtractDiscordName(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("discord_name", out var value)
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _queue.Dispose();
        _client?.Dispose();
    }
}
