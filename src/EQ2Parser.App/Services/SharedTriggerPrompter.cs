using EQ2Parser.App.Localization;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>
/// Handles trigger shares seen in chat: dedupes across mirrored logs
/// (every log in the raid sees the same paste), then either toasts
/// "already have it" or opens the offer window — one at a time, queued.
/// Raised on log-pump threads; everything user-facing marshals to the
/// UI dispatcher.
/// </summary>
public sealed class SharedTriggerPrompter(SourceManager manager)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _recentMs = new(StringComparer.Ordinal);
    private readonly Queue<SharedTrigger> _pending = new();
    private bool _windowOpen;

    private const long DedupeWindowMs = 10_000;

    /// <summary>Log-pump entry point (wired per source).</summary>
    public void OnShared(SharedTrigger share)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_recentMs.TryGetValue(share.Trigger.Key, out var last) && now - last < DedupeWindowMs)
                return; // a mirrored log already surfaced this paste
            if (_recentMs.Count > 64)
            {
                foreach (var stale in _recentMs.Where(kv => now - kv.Value >= DedupeWindowMs).Select(kv => kv.Key).ToList())
                    _recentMs.Remove(stale);
            }
            _recentMs[share.Trigger.Key] = now;
        }
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Surface(share));
    }

    private void Surface(SharedTrigger share)
    {
        // Already known (own custom trigger or a synced Lexicon one): say
        // so in a toast instead of prompting.
        if (manager.Triggers.Definitions.Any(t => t.Key == share.Trigger.Key))
        {
            manager.AnnounceNotification(Loc.Format("Share_AlreadyHave", share.Sharer));
            return;
        }
        lock (_gate)
        {
            if (_windowOpen)
            {
                _pending.Enqueue(share);
                return;
            }
            _windowOpen = true;
        }
        ShowWindow(share);
    }

    private void ShowWindow(SharedTrigger share)
    {
        var window = new Views.ShareOfferWindow(manager, share)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Closed += (_, _) =>
        {
            SharedTrigger? next = null;
            lock (_gate)
            {
                if (_pending.Count > 0)
                    next = _pending.Dequeue();
                else
                    _windowOpen = false;
            }
            if (next is not null)
                Surface2(next);
        };
        window.Show();
    }

    // Re-check "already have it" for queued shares — the user may have just
    // added an identical one from the previous window.
    private void Surface2(SharedTrigger share)
    {
        if (manager.Triggers.Definitions.Any(t => t.Key == share.Trigger.Key))
        {
            manager.AnnounceNotification(Loc.Format("Share_AlreadyHave", share.Sharer));
            SharedTrigger? next = null;
            lock (_gate)
            {
                if (_pending.Count > 0)
                    next = _pending.Dequeue();
                else
                    _windowOpen = false;
            }
            if (next is not null)
                Surface2(next);
            return;
        }
        ShowWindow(share);
    }
}
