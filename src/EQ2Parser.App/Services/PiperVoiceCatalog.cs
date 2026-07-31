using System.Formats.Tar;
using System.IO;
using System.Net.Http;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace EQ2Parser.App.Services;

/// <summary>Model family a neural voice runs on.</summary>
public enum NeuralVoiceKind
{
    PiperVits,
    Kokoro,
}

/// <summary>One downloadable neural voice. Key is the id stored in settings
/// ("piper:"/"kokoro:" prefixes keep them distinct from WinRT voice ids).
/// Kokoro voices share one archive — eleven speakers in a single model,
/// selected by <paramref name="SpeakerId"/> — so installing any of them
/// installs them all.</summary>
public sealed record PiperVoice(
    string Key, string DisplayName, string Archive, string ModelFile, int SizeMb,
    NeuralVoiceKind Kind = NeuralVoiceKind.PiperVits, int SpeakerId = 0);

/// <summary>A downloadable archive and the voices it carries.</summary>
public sealed record NeuralPack(string Archive, string DisplayName, int SizeMb, IReadOnlyList<PiperVoice> PackVoices);

/// <summary>
/// The curated neural voice set and its on-disk lifecycle. Models download
/// once from the sherpa-onnx release assets (permanent, versioned) into
/// %LocalAppData%\EQ2Parser\voices and then work fully offline. Extraction
/// goes through a staging dir and a final atomic move, so a kill mid-install
/// never leaves a voice that looks installed but is missing files.
/// </summary>
public static class PiperVoiceCatalog
{
    private const string KokoroArchive = "kokoro-en-v0_19";

    public static readonly IReadOnlyList<PiperVoice> Voices =
    [
        new("piper:en_GB-alba-medium", "Neural — Alba (British)", "vits-piper-en_GB-alba-medium", "en_GB-alba-medium.onnx", 65),
        new("piper:en_GB-jenny_dioco-medium", "Neural — Jenny (British)", "vits-piper-en_GB-jenny_dioco-medium", "en_GB-jenny_dioco-medium.onnx", 65),
        new("piper:en_GB-northern_english_male-medium", "Neural — Male (Northern English)", "vits-piper-en_GB-northern_english_male-medium", "en_GB-northern_english_male-medium.onnx", 65),
        new("piper:en_US-amy-medium", "Neural — Amy (American)", "vits-piper-en_US-amy-medium", "en_US-amy-medium.onnx", 65),
        new("kokoro:af_bella", "Neural — Bella (warm & sultry)", KokoroArchive, "model.onnx", 305, NeuralVoiceKind.Kokoro, 1),
        new("kokoro:af_nicole", "Neural — Nicole (breathy whisper)", KokoroArchive, "model.onnx", 305, NeuralVoiceKind.Kokoro, 2),
        new("kokoro:am_adam", "Neural — Adam (deep American)", KokoroArchive, "model.onnx", 305, NeuralVoiceKind.Kokoro, 5),
        new("kokoro:bf_emma", "Neural — Emma (British)", KokoroArchive, "model.onnx", 305, NeuralVoiceKind.Kokoro, 7),
        new("kokoro:bf_isabella", "Neural — Isabella (posh British)", KokoroArchive, "model.onnx", 305, NeuralVoiceKind.Kokoro, 8),
    ];

    /// <summary>Downloadable units: one pack per archive (each Piper voice
    /// is its own pack; the Kokoro pack carries five voices).</summary>
    public static IReadOnlyList<NeuralPack> Packs { get; } =
        [.. Voices
            .GroupBy(v => v.Archive)
            .Select(g =>
            {
                List<PiperVoice> packVoices = [.. g];
                var names = string.Join(", ", packVoices.Select(v =>
                {
                    var name = v.DisplayName.Replace("Neural — ", "");
                    var paren = name.IndexOf(" (", StringComparison.Ordinal);
                    return paren > 0 ? name[..paren] : name;
                }));
                return new NeuralPack(g.Key, names, packVoices[0].SizeMb, packVoices);
            })];

    private static string RootDir => Path.Combine(AppSettings.Directory, "voices");

    public static PiperVoice? Find(string? voiceId) =>
        voiceId is null ? null : Voices.FirstOrDefault(v => v.Key == voiceId);

    public static NeuralPack? PackFor(PiperVoice voice) =>
        Packs.FirstOrDefault(p => p.Archive == voice.Archive);

    public static bool IsPackInstalled(NeuralPack pack) => IsInstalled(pack.PackVoices[0]);

    /// <summary>Actual bytes on disk for an installed pack (0 if absent).</summary>
    public static long InstalledBytes(NeuralPack pack)
    {
        var dir = Path.Combine(RootDir, pack.Archive);
        if (!Directory.Exists(dir))
            return 0;
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Delete a pack from disk. The caller must unload the engine
    /// first or the model file is still mapped and the delete fails.</summary>
    public static void RemovePack(NeuralPack pack)
    {
        var dir = Path.Combine(RootDir, pack.Archive);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    public static string ModelDir(PiperVoice voice) =>
        Path.Combine(RootDir, voice.Archive);

    public static string ModelPath(PiperVoice voice) =>
        Path.Combine(ModelDir(voice), voice.ModelFile);

    public static string TokensPath(PiperVoice voice) =>
        Path.Combine(ModelDir(voice), "tokens.txt");

    public static bool IsInstalled(PiperVoice voice) =>
        File.Exists(ModelPath(voice)) && File.Exists(TokensPath(voice));

    /// <summary>Download + install a voice. Progress is 0–1 (download phase;
    /// extraction is a few seconds at the end).</summary>
    public static async Task DownloadAsync(PiperVoice voice, IProgress<double>? progress, CancellationToken ct)
    {
        if (IsInstalled(voice))
            return;
        var url = $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{voice.Archive}.tar.bz2";
        Directory.CreateDirectory(RootDir);
        var archiveFile = Path.Combine(RootDir, $"{voice.Archive}.tar.bz2.partial");
        var staging = Path.Combine(RootDir, $".staging-{voice.Archive}");
        try
        {
            using (var http = new HttpClient())
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? voice.SizeMb * 1024L * 1024L;
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(archiveFile);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }

            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            await Task.Run(() =>
            {
                using var fs = File.OpenRead(archiveFile);
                using var bz = BZip2Stream.Create(fs, CompressionMode.Decompress, false, false, false);
                TarFile.ExtractToDirectory(bz, staging, overwriteFiles: true);
            }, ct);

            // The archive contains one top-level folder named after itself.
            var extracted = Path.Combine(staging, voice.Archive);
            if (!File.Exists(Path.Combine(extracted, voice.ModelFile)))
                throw new InvalidDataException($"Voice archive {voice.Archive} did not contain the expected model.");
            var final = ModelDir(voice);
            if (Directory.Exists(final))
                Directory.Delete(final, recursive: true);
            Directory.Move(extracted, final);
        }
        finally
        {
            try
            {
                if (File.Exists(archiveFile))
                    File.Delete(archiveFile);
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // Leftover staging is re-cleaned on the next attempt.
            }
        }
    }
}
