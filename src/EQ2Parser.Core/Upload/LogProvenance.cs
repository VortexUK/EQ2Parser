using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Upload;

/// <summary>
/// Turns a log-file holder probe into the client_warnings stamped on an
/// upload — the "was the EQ2 process actually writing this log?" signal.
/// Pure so the rules are testable without the Restart Manager.
///
/// Honest limits, by design: a transient writer (a script that opens,
/// appends, closes between probes) is invisible, and holder ≠ writer —
/// this raises the effort bar and gives the site a provenance signal, it
/// is not tamper-proof. Probes run at upload-build time, seconds after the
/// fight ends, so the answer reflects the fight, not some later moment.
/// </summary>
public static class LogProvenance
{
    /// <summary>EQ2's executable name (EverQuest2.exe) as reported by
    /// Process.ProcessName.</summary>
    public const string Eq2ProcessName = "EverQuest2";

    /// <summary>The EQ2 process held the log when the fight was built for
    /// upload — the positive live-log stamp.</summary>
    public const string WriterVerified = "log_writer_eq2";

    /// <summary>No EQ2 process held the log — a backlog parse after the
    /// game closed, or something else entirely. Informative, not damning.</summary>
    public const string WriterUnverified = "log_writer_unverified";

    /// <summary>Prefix for each non-EQ2, non-us process holding the log.</summary>
    public const string ForeignHolderPrefix = "log_foreign_holder:";

    private const int MaxForeignHolders = 4; //   the server caps the list; a
    private const int MaxNameLength = 40; //      few names are plenty

    /// <summary>Warnings for one probe. Always includes exactly one of
    /// <see cref="WriterVerified"/> / <see cref="WriterUnverified"/>, plus a
    /// capped, deduped entry per foreign holder. <paramref name="ownProcessId"/>
    /// filters out our own tail-reader handle.</summary>
    public static List<string> BuildWarnings(IReadOnlyList<FileHolder> holders, int ownProcessId)
    {
        var others = holders.Where(h => h.ProcessId != ownProcessId).ToList();
        var verified = others.Any(h => IsEq2(h.ProcessName));
        List<string> warnings = [verified ? WriterVerified : WriterUnverified];
        foreach (var name in others
                     .Select(h => h.ProcessName)
                     .Where(n => n.Length > 0 && !IsEq2(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(MaxForeignHolders))
        {
            warnings.Add(ForeignHolderPrefix + (name.Length <= MaxNameLength ? name : name[..MaxNameLength]));
        }
        return warnings;
    }

    private static bool IsEq2(string processName) =>
        string.Equals(processName, Eq2ProcessName, StringComparison.OrdinalIgnoreCase);
}
