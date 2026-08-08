using System.IO;

namespace EQ2Parser.App.Services;

/// <summary>
/// Best-effort auto-detection of EQ2 log folders for the first-run wizard.
/// Probes the well-known install layouts on every fixed drive and keeps
/// only folders that actually contain eq2log_*.txt files (any depth — the
/// per-server layout nests them one level down).
/// </summary>
internal static class Eq2LogLocator
{
    private static readonly string[] RelativeCandidates =
    [
        @"SteamLibrary\steamapps\common\EverQuest 2\logs",
        @"Program Files (x86)\Steam\steamapps\common\EverQuest 2\logs",
        @"Steam\steamapps\common\EverQuest 2\logs",
        @"Games\SteamLibrary\steamapps\common\EverQuest 2\logs",
        @"Users\Public\Daybreak Game Company\Installed Games\EverQuest II\logs",
        @"Daybreak Game Company\Installed Games\EverQuest II\logs",
        @"Program Files (x86)\Sony\EverQuest II\logs",
    ];

    /// <summary>Every existing log folder found, most-recently-written
    /// first (the folder they actually play from tops the list).</summary>
    public static List<string> FindLogFolders()
    {
        List<(string Path, DateTime Newest)> found = [];
        foreach (var drive in SafeDrives())
        {
            foreach (var relative in RelativeCandidates)
            {
                var candidate = Path.Combine(drive, relative);
                try
                {
                    if (!Directory.Exists(candidate))
                        continue;
                    var newest = Directory
                        .EnumerateFiles(candidate, "eq2log_*.txt", SearchOption.AllDirectories)
                        .Select(File.GetLastWriteTimeUtc)
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max();
                    if (newest > DateTime.MinValue)
                        found.Add((candidate, newest));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable drive/folder — skip, never crash the wizard.
                }
            }
        }
        return [.. found.OrderByDescending(f => f.Newest).Select(f => f.Path)];
    }

    private static List<string> SafeDrives()
    {
        List<string> roots = [];
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    roots.Add(drive.RootDirectory.FullName);
            }
        }
        catch (IOException)
        {
            roots.Add(@"C:\");
        }
        return roots;
    }
}
