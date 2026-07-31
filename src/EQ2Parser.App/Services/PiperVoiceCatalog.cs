using System.Formats.Tar;
using System.IO;
using System.Net.Http;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace EQ2Parser.App.Services;

/// <summary>One downloadable neural voice (a Piper VITS model run by
/// sherpa-onnx). Key is the id stored in settings ("piper:" prefix keeps it
/// distinct from WinRT voice ids).</summary>
public sealed record PiperVoice(string Key, string DisplayName, string ArchiveId, int SizeMb);

/// <summary>
/// The curated neural voice set and its on-disk lifecycle. Models download
/// once from the sherpa-onnx release assets (permanent, versioned) into
/// %LocalAppData%\EQ2Parser\voices and then work fully offline. Extraction
/// goes through a staging dir and a final atomic move, so a kill mid-install
/// never leaves a voice that looks installed but is missing files.
/// </summary>
public static class PiperVoiceCatalog
{
    public static readonly IReadOnlyList<PiperVoice> Voices =
    [
        new("piper:en_GB-alba-medium", "Neural — Alba (British)", "en_GB-alba-medium", 65),
        new("piper:en_GB-jenny_dioco-medium", "Neural — Jenny (British)", "en_GB-jenny_dioco-medium", 65),
        new("piper:en_GB-northern_english_male-medium", "Neural — Male (Northern English)", "en_GB-northern_english_male-medium", 65),
        new("piper:en_US-amy-medium", "Neural — Amy (American)", "en_US-amy-medium", 65),
    ];

    private static string RootDir => Path.Combine(AppSettings.Directory, "voices");

    public static PiperVoice? Find(string? voiceId) =>
        voiceId is null ? null : Voices.FirstOrDefault(v => v.Key == voiceId);

    public static string ModelDir(PiperVoice voice) =>
        Path.Combine(RootDir, $"vits-piper-{voice.ArchiveId}");

    public static string ModelPath(PiperVoice voice) =>
        Path.Combine(ModelDir(voice), $"{voice.ArchiveId}.onnx");

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
        var url = $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-{voice.ArchiveId}.tar.bz2";
        Directory.CreateDirectory(RootDir);
        var archiveFile = Path.Combine(RootDir, $"{voice.ArchiveId}.tar.bz2.partial");
        var staging = Path.Combine(RootDir, $".staging-{voice.ArchiveId}");
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

            // The archive contains one top-level "vits-piper-<id>" folder.
            var extracted = Path.Combine(staging, $"vits-piper-{voice.ArchiveId}");
            if (!File.Exists(Path.Combine(extracted, $"{voice.ArchiveId}.onnx")))
                throw new InvalidDataException($"Voice archive for {voice.ArchiveId} did not contain the expected model.");
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
