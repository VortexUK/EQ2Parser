using System.Threading.Channels;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Upload;

/// <summary>
/// Serializes encounter uploads off the log-pump thread — the engine's
/// EncounterEnded fires inside the global sync lock, so Enqueue only writes
/// to a channel; payload building and transport happen on the background
/// drain (the HistoryService.QueueSave pattern). Transient failures retry a
/// bounded number of times; a rejected token pauses the queue instead of
/// re-sending a bad credential for every fight.
/// </summary>
public sealed class UploadQueue : IDisposable
{
    private readonly Channel<(Encounter Encounter, string LoggerServer, bool WithProvenance)> _work =
        Channel.CreateUnbounded<(Encounter, string, bool)>();
    private readonly Func<LexiconPayload, CancellationToken, Task<UploadResult>> _send;
    private readonly Func<Encounter, IReadOnlyList<string>?>? _provenance;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drain;
    private volatile bool _authPaused;

    /// <param name="send">Transport — the client's UploadAsync.</param>
    /// <param name="provenance">Optional client_warnings provider (the log
    /// writer probe), invoked on the drain thread right before the payload
    /// builds — never on the pump thread.</param>
    public UploadQueue(
        Func<LexiconPayload, CancellationToken, Task<UploadResult>> send,
        Func<Encounter, IReadOnlyList<string>?>? provenance = null)
    {
        _send = send;
        _provenance = provenance;
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>Waits between attempts after a transient failure (network
    /// error or 5xx) — total attempts = Count + 1. Tests inject zeros.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>Fights uploaded this session (drain thread; reads are for
    /// status display and test polling).</summary>
    public int Uploaded { get; private set; }

    public int Failed { get; private set; }

    /// <summary>True after a 401/403 — work is dropped until the token
    /// changes (<see cref="ResetAuthPause"/>) rather than hammering the
    /// server with a credential it already rejected.</summary>
    public bool AuthPaused => _authPaused;

    /// <summary>Human-readable last event — the Settings card shows it.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Raised from the drain thread whenever Status changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Safe from any thread; never blocks. Pass
    /// <paramref name="withProvenance"/> = false when the fight ended long
    /// ago (manual re-uploads of archived fights) — probing the log NOW
    /// would say nothing about who wrote it THEN, so nothing is claimed.</summary>
    public void Enqueue(Encounter encounter, string loggerServer, bool withProvenance = true)
    {
        if (_authPaused || encounter.Title == Encounter.PlaceholderTitle)
            return;
        _work.Writer.TryWrite((encounter, loggerServer, withProvenance));
    }

    /// <summary>Call when the token changes (or the user explicitly retries)
    /// so a previous rejection stops gating new uploads.</summary>
    public void ResetAuthPause() => _authPaused = false;

    private void Set(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var (encounter, server, withProvenance) in _work.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_authPaused)
                    continue; // drained but dropped — queued before the pause landed
                LexiconPayload payload;
                try
                {
                    IReadOnlyList<string>? warnings = null;
                    if (withProvenance && _provenance is { } provenance)
                    {
                        try
                        {
                            warnings = provenance(encounter);
                        }
                        catch (Exception)
                        {
                            // A failed probe never blocks the upload — the
                            // payload just carries no provenance claim.
                        }
                    }
                    payload = PayloadBuilder.Build(encounter, server, warnings);
                }
                catch (Exception ex)
                {
                    Failed++;
                    Set($"couldn't build the upload for {encounter.Title} ({ex.Message})");
                    continue;
                }
                await SendWithRetryAsync(payload, encounter.Title).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose — drain exits.
        }
    }

    private async Task SendWithRetryAsync(LexiconPayload payload, string title)
    {
        for (var attempt = 0; ; attempt++)
        {
            UploadResult result;
            try
            {
                result = await _send(payload, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The client converts its own failures to UploadResult; this
                // catches the reconfigure race (client disposed mid-send).
                result = new UploadResult(false, 0, $"Network error: {ex.Message}");
            }

            if (result.Success)
            {
                Uploaded++;
                Set($"{title} — {result.Message} {Uploaded} uploaded this session.");
                return;
            }
            if (result.StatusCode is 401 or 403)
            {
                _authPaused = true;
                Failed++;
                Set($"uploads paused — token rejected ({result.StatusCode}). Update it and hit Test.");
                return;
            }
            var transient = result.StatusCode == 0 || result.StatusCode >= 500;
            if (!transient || attempt >= RetryDelays.Count)
            {
                Failed++;
                Set($"{title} upload failed: {result.Message}");
                return;
            }
            Set($"{title} upload retrying — {result.Message}");
            await Task.Delay(RetryDelays[attempt], _cts.Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _work.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _drain.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfacing on shutdown — nothing to report.
        }
        _cts.Dispose();
    }
}
