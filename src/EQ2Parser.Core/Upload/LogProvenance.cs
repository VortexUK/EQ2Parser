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
///
/// Privacy (2026-09-28): the upload never names another process. Until
/// v0.5.6 it stamped <c>log_foreign_holder:&lt;ProcessName&gt;</c>, which
/// told the site what other software was open on the user's PC; 99% of
/// the time that was ACT running alongside. Now EQ2 itself and ACT are
/// expected holders and ignored, and anything else collapses to the bare
/// <see cref="ForeignHolder"/> marker — "something else has the log open",
/// nothing more.
/// </summary>
public static class LogProvenance
{
    /// <summary>EQ2's executable name (EverQuest2.exe) as reported by
    /// Process.ProcessName.</summary>
    public const string Eq2ProcessName = "EverQuest2";

    /// <summary>ACT's executable ("Advanced Combat Tracker.exe") as reported
    /// by Process.ProcessName — the other tool that legitimately tails the
    /// same log, and by far the commonest co-holder.</summary>
    public const string ActProcessName = "Advanced Combat Tracker";

    /// <summary>The EQ2 process held the log when the fight was built for
    /// upload — the positive live-log stamp.</summary>
    public const string WriterVerified = "log_writer_eq2";

    /// <summary>No EQ2 process held the log — a backlog parse after the
    /// game closed, or something else entirely. Informative, not damning.</summary>
    public const string WriterUnverified = "log_writer_unverified";

    /// <summary>Some process other than EQ2, ACT or us held the log. One
    /// bare marker — never a name, never a count.</summary>
    public const string ForeignHolder = "log_foreign_holder";

    /// <summary>Warnings for one probe. Always includes exactly one of
    /// <see cref="WriterVerified"/> / <see cref="WriterUnverified"/>, plus
    /// <see cref="ForeignHolder"/> when any unexpected process holds the
    /// log. <paramref name="ownProcessId"/> filters out our own tail-reader
    /// handle.</summary>
    public static List<string> BuildWarnings(IReadOnlyList<FileHolder> holders, int ownProcessId)
    {
        var others = holders.Where(h => h.ProcessId != ownProcessId).ToList();
        var verified = others.Any(h => IsEq2(h.ProcessName));
        List<string> warnings = [verified ? WriterVerified : WriterUnverified];
        if (others.Any(h => h.ProcessName.Length > 0 && !IsEq2(h.ProcessName) && !IsAct(h.ProcessName)))
            warnings.Add(ForeignHolder);
        return warnings;
    }

    private static bool IsEq2(string processName) =>
        string.Equals(processName, Eq2ProcessName, StringComparison.OrdinalIgnoreCase);

    private static bool IsAct(string processName) =>
        string.Equals(processName, ActProcessName, StringComparison.OrdinalIgnoreCase);
}
