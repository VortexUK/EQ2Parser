using System.Text.Json;

namespace EQ2Parser.Core.Persistence;

/// <summary>
/// The one way app state reaches disk. Every hand-rolled store shared the
/// same silent-data-loss shape (in-place WriteAllText + swallow-all load +
/// next save overwrites), so:
///   * writes are ATOMIC — temp file then File.Replace, so a crash or
///     power cut mid-write can never leave a torn/empty file;
///   * writes are ORDERED per path — one gate per file, and the debounced
///     writer captures its snapshot INSIDE that gate, so the newest state
///     always lands last (a stale snapshot can never overwrite a fresh one);
///   * a file that fails to LOAD is QUARANTINED (renamed *.corrupt-*)
///     instead of being left in place for the next save to overwrite —
///     the bytes survive for manual recovery;
///   * <see cref="SaveSoon{T}"/> debounces high-frequency callers (slider
///     drags) onto a single trailing write; a direct <see cref="Save{T}"/>
///     of the same path cancels the pending write (it carries newer state),
///     and <see cref="FlushPending"/> runs the stragglers at app exit.
/// Cross-PROCESS races are not defended: the fixed .tmp name means a second
/// running instance fails loudly (IOException) rather than corrupting data,
/// and a crash-orphaned .tmp is overwritten by the next save.
/// </summary>
public static class PersistedJsonFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Dictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PendingGate = new();
    private static readonly Dictionary<string, PendingSave> Pending =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class PendingSave
    {
        public Timer Timer = null!;
        /// <summary>Serialize + write, no Pending bookkeeping. Replaced on
        /// every SaveSoon call so the LATEST factory wins even if a caller
        /// ever passes a captured snapshot instead of a live read.</summary>
        public Action Write = static () => { };
    }

    private static object GateFor(string path)
    {
        lock (Gates)
        {
            if (!Gates.TryGetValue(path, out var gate))
                Gates[path] = gate = new object();
            return gate;
        }
    }

    /// <summary>Load or fall back. A present-but-unreadable file is moved
    /// aside (never silently overwritten by the next save). Takes the path
    /// gate so a load can never race a mid-Replace write of the same file
    /// and quarantine a perfectly healthy one.</summary>
    public static T Load<T>(string path, Func<T> fallback)
    {
        lock (GateFor(path))
        {
            SweepStaleTmp(path);
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
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        // A direct Save subsumes any pending debounced write of the same
        // path — the debounce factory reads live state, and this call's
        // value IS that state (or newer). Cancel, don't double-write.
        lock (PendingGate)
        {
            if (Pending.Remove(path, out var pending))
                pending.Timer.Dispose();
        }
        lock (GateFor(path))
        {
            WriteAtomic(path, JsonSerializer.Serialize(value, options ?? Indented));
        }
    }

    /// <summary>Coalesce rapid calls onto one write ~500ms after the last.
    /// The value factory runs at write time, inside the path gate, so it
    /// captures final state and write order equals state order.</summary>
    public static void SaveSoon<T>(string path, Func<T> value, JsonSerializerOptions? options = null)
    {
        lock (PendingGate)
        {
            if (!Pending.TryGetValue(path, out var pending))
            {
                pending = new PendingSave();
                pending.Timer = new Timer(_ => Flush(path));
                Pending[path] = pending;
            }
            pending.Write = () =>
            {
                lock (GateFor(path))
                {
                    WriteAtomic(path, JsonSerializer.Serialize(value(), options ?? Indented));
                }
            };
            pending.Timer.Change(500, Timeout.Infinite);
        }
    }

    /// <summary>Run one path's pending write now (timer callback / exit).</summary>
    private static void Flush(string path)
    {
        PendingSave? pending;
        lock (PendingGate)
        {
            // Already flushed (exit) or cancelled (direct Save).
            if (!Pending.Remove(path, out pending))
                return;
            pending.Timer.Dispose();
        }
        try
        {
            pending.Write();
        }
        catch (Exception)
        {
            // Degrades to in-memory state, same as callers always did.
        }
    }

    /// <summary>Run every pending debounced write NOW, synchronously —
    /// call on app exit so a change made within the debounce window of
    /// quitting still reaches disk.</summary>
    public static void FlushPending()
    {
        List<string> paths;
        lock (PendingGate)
        {
            paths = [.. Pending.Keys];
        }
        foreach (var path in paths)
            Flush(path);
    }

    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Fixed tmp name, on purpose: the path gate serializes in-process
        // writers, and a crash-orphaned .tmp is overwritten by the next
        // save instead of littering the directory forever.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }

    /// <summary>One release (v0.2.8) wrote per-thread tmp names
    /// (settings.json.14.tmp); a failed save stranded them permanently.
    /// Best-effort sweep on load.</summary>
    private static void SweepStaleTmp(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            foreach (var stale in Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp"))
                File.Delete(stale);
        }
        catch (Exception)
        {
            // Best-effort cleanup — never let it break a load.
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
