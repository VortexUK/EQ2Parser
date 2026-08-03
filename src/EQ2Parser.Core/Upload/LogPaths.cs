namespace EQ2Parser.Core.Upload;

/// <summary>
/// Pure parser for the EQ2 log path shape
/// &lt;install&gt;/logs/&lt;server&gt;/eq2log_&lt;character&gt;.txt. Mirrors the ACT
/// plugin's LogPathParser so both uploaders stamp identical logger_server
/// values on their payloads.
/// </summary>
public static class LogPaths
{
    /// <summary>
    /// EQ2 server name from a log path — the log's parent directory name.
    /// Returns "" when the path doesn't match the per-server layout — the
    /// legacy generic log at &lt;install&gt;/logs/eq2log.txt has parent "logs",
    /// not a server — and the site then falls back to its configured
    /// default world. Server names with spaces ("Antonia Bayle") pass
    /// through unmodified.
    /// </summary>
    public static string ParseServerName(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return "";
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(logPath);
        }
        catch (ArgumentException)
        {
            return ""; // embedded NUL or similarly hostile path — unknown
        }
        if (string.IsNullOrEmpty(dir))
            return "";
        var name = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(name) || name.Equals("logs", StringComparison.OrdinalIgnoreCase))
            return "";
        return name;
    }
}
