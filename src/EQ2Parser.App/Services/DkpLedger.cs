using System.IO;

namespace EQ2Parser.App.Services;

/// <summary>Append-only audit trail of every CONFIRMED guild-points change
/// (award grants and loot deductions, written the moment their officer-chat
/// echo confirms them in the log). The safety net when a raid night's DKP
/// needs reconstructing — each line is the exact game command that ran.
/// Lives at %LocalAppData%\EQ2Parser\dkp-ledger.log; never trimmed by the
/// app.</summary>
public static class DkpLedger
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppSettings.Directory, "dkp-ledger.log");

    public static void Append(string entry)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Directory);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {entry}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The ledger is best-effort — never let audit IO break a raid.
        }
    }
}
