using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Views;

/// <summary>One visible toast.</summary>
public sealed partial class NotificationRow : ObservableObject
{
    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private double _opacity = 1;
}

/// <summary>
/// The Notifications overlay: every trigger fire pops up as a toast for a
/// few seconds, then fades out. The spoken callout is the toast text —
/// what you'd have heard, now also visible; repeats of the same callout
/// collapse into a ×N counter instead of stacking.
/// </summary>
public partial class NotificationsContent : IOverlayContent
{
    private sealed class Toast(string text, DateTimeOffset first)
    {
        public string Text { get; } = text;
        public DateTimeOffset First { get; } = first;
        public DateTimeOffset Last { get; set; } = first;
        public int Count { get; set; } = 1;
    }

    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(150);

    /// <summary>User-configurable stay duration (the overlay card's
    /// "stay for" slider); refreshed from settings each tick, read on the
    /// log pump thread for the repeat-merge window.</summary>
    private TimeSpan _hold = TimeSpan.FromSeconds(6);

    private readonly SourceManager _manager;
    private readonly object _gate = new();
    private readonly List<Toast> _toasts = [];
    private readonly ObservableCollection<NotificationRow> _rows = [];

    public NotificationsContent(SourceManager manager)
    {
        _manager = manager;
        InitializeComponent();
        Items.ItemsSource = _rows;
        // AlertFired arrives on the log pump thread — the handler only
        // touches the locked toast list; the UI syncs on Refresh ticks.
        manager.Triggers.AlertFired += OnFired;
        manager.CalloutAnnounced += AddToast;
        Unloaded += (_, _) =>
        {
            _manager.Triggers.AlertFired -= OnFired;
            _manager.CalloutAnnounced -= AddToast;
        };
    }

    private void OnFired(TriggerFired fired)
    {
        if (TextFor(fired) is { } text)
            AddToast(text);
    }

    private void AddToast(string text)
    {
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            if (_toasts.FirstOrDefault(t => t.Text == text && now - t.Last <= _hold) is { } existing)
            {
                existing.Count++;
                existing.Last = now;
            }
            else
            {
                _toasts.Add(new Toast(text, now));
            }
        }
    }

    /// <summary>The toast text is the CALLOUT: spoken TTS (captures
    /// expanded) when the trigger speaks; otherwise the matched line for
    /// beep/WAV triggers, or the timer name for silent timer-starters.
    /// Null (no toast) for a TTS trigger inside its audio cooldown — the
    /// popup respects the same rate limit as the voice.</summary>
    private static string? TextFor(TriggerFired fired)
    {
        if (fired.TtsText is { Length: > 0 } tts)
            return Shorten(tts);
        if (fired.PlayBeep || fired.WavFile is { Length: > 0 })
            return Shorten(fired.Match.Value.Length > 0 ? fired.Match.Value : fired.Trigger.RegexText);
        if (fired.Trigger.SoundType == TriggerSound.None)
            return fired.Trigger is { StartsTimer: true, TimerName.Length: > 0 } t
                ? Shorten(t.TimerName)
                : Shorten(fired.Match.Value.Length > 0 ? fired.Match.Value : fired.Trigger.RegexText);
        return null;
    }

    private static string Shorten(string text) =>
        text.Length <= 90 ? text : text[..89] + "…";

    public bool Refresh(OverlayWindowSettings settings)
    {
        var now = DateTimeOffset.Now;
        var hold = TimeSpan.FromSeconds(Math.Clamp(settings.ToastSeconds, 2, 30));
        List<(string Text, string Count, double Opacity)> visible = [];
        lock (_gate)
        {
            _hold = hold;
            _toasts.RemoveAll(t => now - t.Last > hold + FadeOut);
            foreach (var toast in _toasts.OrderByDescending(t => t.Last).Take(settings.MaxItems))
            {
                var sinceLast = now - toast.Last;
                var opacity = sinceLast <= hold
                    ? 1.0
                    : 1.0 - (sinceLast - hold) / FadeOut;
                var fadeIn = (now - toast.First) / FadeIn;
                opacity = Math.Clamp(Math.Min(opacity, fadeIn), 0, 1);
                visible.Add((toast.Text, toast.Count > 1 ? $"×{toast.Count}" : "", opacity));
            }
        }
        while (_rows.Count > visible.Count)
            _rows.RemoveAt(_rows.Count - 1);
        while (_rows.Count < visible.Count)
            _rows.Add(new NotificationRow());
        for (var i = 0; i < visible.Count; i++)
        {
            _rows[i].Text = visible[i].Text;
            _rows[i].CountText = visible[i].Count;
            _rows[i].Opacity = visible[i].Opacity;
        }
        EmptyHint.Visibility = visible.Count == 0 && !settings.Locked ? Visibility.Visible : Visibility.Collapsed;
        return visible.Count > 0;
    }
}
