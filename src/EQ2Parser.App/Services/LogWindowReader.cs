using System.IO;
using System.Text;

namespace EQ2Parser.App.Services;

/// <summary>
/// Reads a time window of raw lines out of a (potentially huge) EQ2 log by
/// binary-searching the leading "(epoch)" stamps — no full-file scan.
/// </summary>
public static class LogWindowReader
{
    public static IReadOnlyList<string> Read(string path, long epoch, int beforeSeconds, int afterSeconds, int maxPerSide = 200)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = FindOffsetForEpoch(stream, epoch - beforeSeconds);
            stream.Position = start;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            // Budget the lines AROUND the target: keep the LAST maxPerSide
            // before the clicked second and the first maxPerSide from it —
            // raid seconds can hold hundreds of lines, and a flat cap used
            // to fill up before ever reaching the clicked swing.
            var before = new Queue<string>();
            List<string> fromTarget = [];
            while (reader.ReadLine() is { } line)
            {
                var lineEpoch = ParseEpoch(line);
                if (lineEpoch < 0)
                    continue;
                if (lineEpoch > epoch + afterSeconds)
                    break;
                if (lineEpoch < epoch - beforeSeconds)
                    continue;
                if (lineEpoch < epoch)
                {
                    before.Enqueue(line);
                    if (before.Count > maxPerSide)
                        before.Dequeue();
                }
                else
                {
                    fromTarget.Add(line);
                    if (fromTarget.Count >= maxPerSide)
                        break;
                }
            }
            return [.. before, .. fromTarget];
        }
        catch (Exception)
        {
            // Missing/rotated file — the log view just shows nothing.
            return [];
        }
    }

    /// <summary>Byte offset of a line at-or-before the first line whose
    /// epoch is >= <paramref name="epoch"/>.</summary>
    private static long FindOffsetForEpoch(FileStream stream, long epoch)
    {
        long lo = 0, hi = stream.Length;
        var buffer = new byte[512];
        while (hi - lo > 4096)
        {
            var mid = lo + (hi - lo) / 2;
            var lineStart = NextLineStart(stream, mid, buffer);
            if (lineStart >= hi)
            {
                hi = mid;
                continue;
            }
            var midEpoch = ReadEpochAt(stream, lineStart, buffer);
            if (midEpoch < 0 || midEpoch < epoch)
                lo = lineStart;
            else
                hi = mid;
        }
        return lo;
    }

    private static long NextLineStart(FileStream stream, long from, byte[] buffer)
    {
        stream.Position = from;
        long offset = from;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                    return offset + i + 1;
            }
            offset += read;
        }
        return stream.Length;
    }

    private static long ReadEpochAt(FileStream stream, long position, byte[] buffer)
    {
        stream.Position = position;
        var read = stream.Read(buffer, 0, 16);
        if (read < 3 || buffer[0] != (byte)'(')
            return -1;
        long epoch = 0;
        for (var i = 1; i < read; i++)
        {
            if (buffer[i] == (byte)')')
                return epoch;
            if (buffer[i] is < (byte)'0' or > (byte)'9')
                return -1;
            epoch = epoch * 10 + (buffer[i] - (byte)'0');
        }
        return -1;
    }

    private static long ParseEpoch(string line)
    {
        if (line.Length < 3 || line[0] != '(')
            return -1;
        long epoch = 0;
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == ')')
                return epoch;
            if (line[i] is < '0' or > '9')
                return -1;
            epoch = epoch * 10 + (line[i] - '0');
        }
        return -1;
    }
}
