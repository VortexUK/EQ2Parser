using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EQ2Parser.Core.Logs;

/// <summary>A process holding an open handle to a file.</summary>
public readonly record struct FileHolder(int ProcessId, string ProcessName);

/// <summary>
/// Who has this file open? Answered via the Windows Restart Manager — the
/// SUPPORTED api for "which processes are using this file" (the same one
/// installers use). It reports handle *holders*, not access modes: Windows
/// has no supported way to enumerate write handles specifically (that's
/// undocumented NtQuerySystemInformation territory — fragile and slow), but
/// EQ2 keeps its log open for append the whole time /log is on, so "the
/// EverQuest2 process is among the holders" IS the live-writer signal.
/// A probe costs a few milliseconds — fine once per fight, never per line.
/// </summary>
public static class LogFileHolders
{
    private const int ErrorMoreData = 234;

    /// <summary>Processes currently holding <paramref name="path"/> open —
    /// including our own tail reader (callers filter by pid). Empty on any
    /// failure, on a missing file, and always on non-Windows.</summary>
    public static IReadOnlyList<FileHolder> Probe(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
            return [];
        return ProbeWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<FileHolder> ProbeWindows(string path)
    {
        if (RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0)
            return [];
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0)
                return [];
            uint count = 0;
            uint reasons = 0;
            var result = RmGetList(session, out var needed, ref count, null, ref reasons);
            // The holder set can change between the size query and the fetch;
            // a couple of retries absorb that race.
            for (var retry = 0; result == ErrorMoreData && retry < 3; retry++)
            {
                var infos = new RmProcessInfo[needed];
                count = needed;
                result = RmGetList(session, out needed, ref count, infos, ref reasons);
                if (result == 0)
                {
                    var holders = new List<FileHolder>((int)count);
                    for (var i = 0; i < count; i++)
                        holders.Add(new FileHolder(infos[i].Process.ProcessId, ResolveName(infos[i])));
                    return holders;
                }
            }
            return []; // result == 0 with nothing needed → no holders
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    /// <summary>The executable name beats Restart Manager's friendly app
    /// name ("EverQuest II") for matching — fall back to the friendly name
    /// when the process exited between the probe and the lookup.</summary>
    [SupportedOSPlatform("windows")]
    private static string ResolveName(in RmProcessInfo info)
    {
        try
        {
            using var process = Process.GetProcessById(info.Process.ProcessId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return info.AppName ?? "";
        }
        catch (InvalidOperationException)
        {
            return info.AppName ?? "";
        }
    }

    // ── Restart Manager interop (rstrtmgr.dll) ──────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount, string[] filenames,
        uint applicationCount, RmUniqueProcess[]? applications,
        uint serviceCount, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint neededCount, ref uint infoCount,
        [In, Out] RmProcessInfo[]? processInfo,
        ref uint rebootReasons);
}
