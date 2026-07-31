using System.IO;
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
    private string? _loadedKey;

    /// <summary>Synthesize to 16-bit WAV bytes, or null when the model
    /// can't load or produces nothing.</summary>
    public byte[]? Synthesize(PiperVoice voice, string text, double rate)
    {
        lock (_gate)
        {
            try
            {
                if (_loadedKey != voice.Key)
                {
                    _tts?.Dispose();
                    _tts = null;
                    _loadedKey = null;
                    var config = new OfflineTtsConfig();
                    config.Model.Vits.Model = PiperVoiceCatalog.ModelPath(voice);
                    config.Model.Vits.Tokens = PiperVoiceCatalog.TokensPath(voice);
                    var espeakData = Path.Combine(PiperVoiceCatalog.ModelDir(voice), "espeak-ng-data");
                    if (Directory.Exists(espeakData))
                        config.Model.Vits.DataDir = espeakData;
                    config.Model.NumThreads = 2;
                    _tts = new OfflineTts(config);
                    _loadedKey = voice.Key;
                }

                var audio = _tts!.Generate(text, (float)Math.Clamp(rate, 0.5, 3.0), 0);
                if (audio.Samples.Length == 0)
                    return null;
                using var ms = new MemoryStream();
                using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(audio.SampleRate, 16, 1)))
                {
                    writer.WriteSamples(audio.Samples, 0, audio.Samples.Length);
                }
                return ms.ToArray();
            }
            catch (Exception)
            {
                // A corrupt model or native failure degrades to the Windows
                // voice fallback in AlertAudioService.
                _tts?.Dispose();
                _tts = null;
                _loadedKey = null;
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tts?.Dispose();
            _tts = null;
        }
    }
}
