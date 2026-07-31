using System.IO;
using System.Threading.Channels;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.SpeechSynthesis;

namespace EQ2Parser.App.Services;

/// <summary>A selectable TTS voice (WinRT identity + friendly name).</summary>
public sealed record TtsVoice(string Id, string DisplayName);

/// <summary>
/// The alert audio stack. TTS goes through WinRT speech
/// (Windows.Media.SpeechSynthesis) — the modern OneCore/natural voices, not
/// System.Speech's ancient SAPI Desktop set — synthesized to WAV bytes
/// (cached per voice/rate/phrase, so repeats play instantly) and played via
/// NAudio. Spoken alerts queue so phrases never talk over each other; the
/// chime and WAV-file alerts fire immediately and may overlap speech.
/// The "beep" is a synthesized soft bell, not the harsh system exclamation.
/// </summary>
public sealed class AlertAudioService : IDisposable
{
    private const int CacheCap = 300;

    private readonly Channel<Func<CancellationToken, Task>> _speech =
        Channel.CreateBounded<Func<CancellationToken, Task>>(
            new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, byte[]> _ttsCache = [];
    private readonly object _cacheGate = new();
    private readonly byte[] _chime = BuildChime();
    private readonly Task _speechPump;

    /// <summary>Master alert volume, 0–1.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Speaking rate multiplier (0.5 = half speed, 2 = double).</summary>
    public double SpeakingRate { get; set; } = 1.0;

    /// <summary>WinRT voice Id; null picks the best default (natural voice
    /// when the system exposes one, else the system default).</summary>
    public string? VoiceId { get; set; }

    public AlertAudioService()
    {
        _speechPump = Task.Run(async () =>
        {
            await foreach (var job in _speech.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await job(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // One bad phrase or a device hiccup never stops the pump.
                }
            }
        });
    }

    /// <summary>Every installed voice, natural voices first.</summary>
    public static IReadOnlyList<TtsVoice> ListVoices()
    {
        try
        {
            return [.. SpeechSynthesizer.AllVoices
                .Select(v => new TtsVoice(v.Id, v.DisplayName))
                .OrderByDescending(v => v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase))
                .ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Queue a phrase — spoken after anything already queued.</summary>
    public void Speak(string text)
    {
        if (text.Length == 0)
            return;
        _speech.Writer.TryWrite(async ct =>
        {
            var wav = await SynthesizeAsync(text);
            if (wav is not null)
                await PlayBytesAsync(wav, ct);
        });
    }

    /// <summary>The soft-bell alert. Fire-and-forget, overlaps speech.</summary>
    public void PlayChime() =>
        _ = PlayBytesAsync(_chime, _cts.Token);

    /// <summary>Play an audio file (WAV/MP3). Fire-and-forget.</summary>
    public void PlayFile(string path)
    {
        if (!File.Exists(path))
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new AudioFileReader(path);
                reader.Volume = (float)Math.Clamp(Volume, 0, 1);
                await PlayProviderAsync(reader, _cts.Token, applyVolume: false);
            }
            catch (Exception)
            {
                // Unreadable/corrupt file — alert is silently skipped.
            }
        });
    }

    private async Task<byte[]?> SynthesizeAsync(string text)
    {
        var key = $"{VoiceId}|{SpeakingRate:0.##}|{text}";
        lock (_cacheGate)
        {
            if (_ttsCache.TryGetValue(key, out var cached))
                return cached;
        }
        try
        {
            using var synth = new SpeechSynthesizer();
            var voice = ResolveVoice(VoiceId);
            if (voice is not null)
                synth.Voice = voice;
            synth.Options.SpeakingRate = Math.Clamp(SpeakingRate, 0.5, 6.0);
            using var stream = await synth.SynthesizeTextToStreamAsync(text);
            using var net = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            var wav = ms.ToArray();
            lock (_cacheGate)
            {
                if (_ttsCache.Count >= CacheCap)
                    _ttsCache.Clear();
                _ttsCache[key] = wav;
            }
            return wav;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static VoiceInformation? ResolveVoice(string? id)
    {
        var voices = SpeechSynthesizer.AllVoices;
        if (id is not null && voices.FirstOrDefault(v => v.Id == id) is { } chosen)
            return chosen;
        // Prefer a natural voice when the system exposes one to apps.
        return voices.FirstOrDefault(v => v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase));
    }

    private async Task PlayBytesAsync(byte[] wav, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(wav);
            using var reader = new WaveFileReader(ms);
            await PlayProviderAsync(reader.ToSampleProvider(), ct, applyVolume: true);
        }
        catch (Exception)
        {
            // No audio device / driver hiccup — never propagate.
        }
    }

    private async Task PlayProviderAsync(ISampleProvider provider, CancellationToken ct, bool applyVolume)
    {
        if (applyVolume)
            provider = new VolumeSampleProvider(provider) { Volume = (float)Math.Clamp(Volume, 0, 1) };
        using var output = new WaveOutEvent();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) => done.TrySetResult();
        output.Init(provider);
        output.Play();
        await using var reg = ct.Register(() =>
        {
            try
            {
                output.Stop();
            }
            catch (Exception)
            {
                // Racing a device teardown on shutdown.
            }
            done.TrySetResult();
        });
        await done.Task;
    }

    /// <summary>A short two-partial bell with a soft attack and natural
    /// decay — pleasant at raid volume, unlike SystemSounds.</summary>
    private static byte[] BuildChime()
    {
        const int rate = 44100;
        const double seconds = 0.5;
        var count = (int)(rate * seconds);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / rate;
            var envelope = Math.Min(1.0, t / 0.006) * Math.Exp(-t * 7.0);
            var tone = 0.60 * Math.Sin(2 * Math.PI * 987.77 * t)   // B5
                     + 0.28 * Math.Sin(2 * Math.PI * 1975.53 * t)  // B6 partial
                     + 0.12 * Math.Sin(2 * Math.PI * 1479.98 * t); // F#6 shimmer
            samples[i] = (float)(0.45 * envelope * tone);
        }
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(rate, 16, 1)))
        {
            writer.WriteSamples(samples, 0, samples.Length);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        _speech.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _speechPump.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces here on shutdown.
        }
        _cts.Dispose();
    }
}
