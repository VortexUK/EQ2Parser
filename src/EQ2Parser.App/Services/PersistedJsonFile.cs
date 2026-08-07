using System.IO;
using System.Linq;
using System.Text.Json;

namespace EQ2Parser.App.Services;

/// <summary>
/// The one way app state reaches disk. Every hand-rolled store shared the
/// same silent-data-loss shape (in-place WriteAllText + swallow-all load +
/// next save overwrites), so:
///   * writes are ATOMIC — temp file then File.Replace, so a crash or
///     power cut mid-write can never leave a torn/empty file;
///   * a file that fails to LOAD is QUARANTINED (renamed *.corrupt-*)
///     instead of being left in place for the next save to overwrite —
///     the bytes survive for manual recovery;
///   * <see cref="SaveSoon{T}"/> debounces high-frequency callers (slider
///     drags) onto a single trailing write.
/// </summary>
public static class PersistedJsonFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Timer> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Load or fall back. A present-but-unreadable file is moved
    /// aside (never silently overwritten by the next save).</summary>
    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path))
                return fallback();
            var loaded = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            if (loaded is not null)
                return loaded;
        }
        catch (Exception)
        {
            // Unreadable — quarantine below so the evidence survives.
        }
        Quarantine(path);
        return fallback();
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        // Serialize outside the lock (cheap but why hold it), write inside:
        // a synchronous Save racing the SaveSoon timer thread used to
        // collide on the FIXED .tmp name (IOException at startup when the
        // overlay restore saved while a debounced write was in flight).
        // The unique tmp suffix is belt-and-braces for anything outside
        // this process.
        var json = JsonSerializer.Serialize(value, options ?? Indented);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
    }

    /// <summary>Coalesce rapid calls onto one write ~500ms after the last.
    /// The value factory runs at write time, so it captures final state.</summary>
    public static void SaveSoon<T>(string path, Func<T> value, JsonSerializerOptions? options = null)
    {
        lock (Gate)
        {
            if (Pending.TryGetValue(path, out var existing))
            {
                existing.Change(500, Timeout.Infinite);
                return;
            }
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                lock (Gate)
                {
                    if (Pending.Remove(path, out var t))
                        t.Dispose();
                }
                try
                {
                    Save(path, value(), options);
                }
                catch (Exception)
                {
                    // Degrades to in-memory state, same as callers always did.
                }
            });
            Pending[path] = timer;
            timer.Change(500, Timeout.Infinite);
        }
    }

    private static void Quarantine(string path)
    {
        try
        {
            File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            PruneQuarantine(path);
        }
        catch (Exception)
        {
            // Locked or gone — nothing more we can do without blocking startup.
        }
    }

    private const int KeepQuarantineFiles = 5;

    /// <summary>Keep only the newest few *.corrupt-* files per base path.
    /// Unbounded, these accumulate forever — and once a token or other secret
    /// lives in the persisted file, each quarantine copy is a lingering
    /// plaintext copy of it.</summary>
    private static void PruneQuarantine(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                return;
            var stale = new DirectoryInfo(dir)
                .GetFiles(Path.GetFileName(path) + ".corrupt-*")
                .OrderByDescending(f => f.Name)
                .Skip(KeepQuarantineFiles);
            foreach (var f in stale)
                f.Delete();
        }
        catch (Exception)
        {
            // Best-effort cleanup — never let it break a save.
        }
    }
}
