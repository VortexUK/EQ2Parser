using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Utils;
using NAudio.Wave;
using SherpaOnnx;

namespace EQ2Parser.App.Services;

/// <summary>
/// The neural TTS backend: runs downloaded Piper VITS models through
/// sherpa-onnx on the CPU (~100 ms per phrase warm; the phoneme data is
/// embedded in the native library). One model stays loaded at a time —
/// loading takes a second or two, so the first phrase after a voice switch
/// is slower and everything after is instant.
/// </summary>
public sealed class PiperTtsEngine : IDisposable
{
    private readonly object _gate = new();
    private OfflineTts? _tts;
    private string? _loadedArchive;

    /// <summary>Why the last synthesis returned null (failure stage +
    /// exception, or "produced no audio") — null after a success. Lets the
    /// caller tell the user WHY alerts fell back to a Windows voice instead
    /// of failing silently.</summary>
    public string? LastError { get; private set; }

    /// <summary>Synthesize to 16-bit WAV bytes, or null when the model
    /// can't load or produces nothing.</summary>
    public byte[]? Synthesize(PiperVoice voice, string text, double rate)
    {
        lock (_gate)
        {
            var stage = "loading the model";
            try
            {
                // Cache by archive: Kokoro packs eleven speakers into one
                // model, so switching between them never reloads.
                if (_loadedArchive != voice.Archive)
                {
                    _tts?.Dispose();
                    _tts = null;
                    _loadedArchive = null;
                    var config = new OfflineTtsConfig();
                    // The native layer receives paths as raw bytes and cannot
                    // open non-ASCII ones (a username like Jürgen breaks every
                    // model load with a bare NullReferenceException) — hand it
                    // the ASCII 8.3 short path instead when needed.
                    var modelDir = AsciiSafePath(PiperVoiceCatalog.ModelDir(voice));
                    var espeakData = Path.Combine(modelDir, "espeak-ng-data");
                    if (voice.Kind == NeuralVoiceKind.Kokoro)
                    {
                        config.Model.Kokoro.Model = Path.Combine(modelDir, voice.ModelFile);
                        config.Model.Kokoro.Voices = Path.Combine(modelDir, "voices.bin");
                        config.Model.Kokoro.Tokens = Path.Combine(modelDir, "tokens.txt");
                        if (Directory.Exists(espeakData))
                            config.Model.Kokoro.DataDir = espeakData;
                    }
                    else
                    {
                        config.Model.Vits.Model = Path.Combine(modelDir, voice.ModelFile);
                        config.Model.Vits.Tokens = Path.Combine(modelDir, "tokens.txt");
                        if (Directory.Exists(espeakData))
                            config.Model.Vits.DataDir = espeakData;
                    }
                    config.Model.NumThreads = 2;
                    _tts = new OfflineTts(config);
                    _loadedArchive = voice.Archive;
                }

                stage = "synthesis";
                var audio = _tts!.Generate(text, (float)Math.Clamp(rate, 0.5, 3.0), voice.SpeakerId);
                if (audio?.Samples is not { Length: > 0 } samples)
                {
                    LastError = "the model produced no audio";
                    return null;
                }
                using var ms = new MemoryStream();
                using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(audio.SampleRate, 16, 1)))
                {
                    writer.WriteSamples(samples, 0, samples.Length);
                }
                LastError = null;
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                // A corrupt model or native failure degrades to the Windows
                // voice fallback in AlertAudioService — record the STAGE and
                // why, so the Settings page can say so instead of pretending.
                var hint = "";
                var dir = PiperVoiceCatalog.ModelDir(voice);
                if (dir.Any(c => c > 127) && AsciiSafePath(dir) == dir)
                    hint = " — the voice folder path contains non-ASCII characters the speech engine cannot read, and no ASCII short path exists for it";
                LastError = $"{stage} failed — {ex.GetType().Name}: {ex.Message}{hint}";
                _tts?.Dispose();
                _tts = null;
                _loadedArchive = null;
                return null;
            }
        }
    }

    /// <summary>Windows 8.3 short path when the real one contains non-ASCII
    /// characters (native fopen can't take them); the original path when it
    /// is already ASCII, off-Windows, or the volume has 8.3 names disabled.</summary>
    private static string AsciiSafePath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.All(c => c < 128))
            return path;
        var buffer = new StringBuilder(520);
        var n = GetShortPathName(path, buffer, buffer.Capacity);
        if (n <= 0 || n > buffer.Capacity)
            return path;
        var shortPath = buffer.ToString();
        return shortPath.All(c => c < 128) ? shortPath : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);

    /// <summary>Release the loaded model (file handles included) so its
    /// pack can be deleted from disk. Next synthesis reloads on demand.</summary>
    public void Unload()
    {
        lock (_gate)
        {
            _tts?.Dispose();
            _tts = null;
            _loadedArchive = null;
        }
    }

    public void Dispose() => Unload();
}
