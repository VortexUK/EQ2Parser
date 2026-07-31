using System.Windows.Media;
using EQ2Parser.Core.Grammar;
using EQ2Parser.Core.Logs;

namespace EQ2Parser.App.Services;

public sealed record LogSegment(string Text, Brush Brush);

/// <summary>
/// Colourises a raw log line by re-parsing it with the grammar and painting
/// the parts: timestamp dim, attacker gold, ability blue, amount bright
/// gold, victim normal, deaths red; unparsed flavour stays muted.
/// </summary>
public static class LogLineHighlighter
{
    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush Time = Frozen("#565B78");
    private static readonly SolidColorBrush Plain = Frozen("#B9BDD2");
    private static readonly SolidColorBrush Flavour = Frozen("#6A6F8C");
    private static readonly SolidColorBrush Attacker = Frozen("#C8A96E");
    private static readonly SolidColorBrush Ability = Frozen("#93B4FF");
    private static readonly SolidColorBrush Amount = Frozen("#E8D5A3");
    private static readonly SolidColorBrush Victim = Frozen("#E2E4F0");
    private static readonly SolidColorBrush Death = Frozen("#F87171");

    public static IReadOnlyList<LogSegment> Build(string raw)
    {
        if (!LogLine.TryParse(raw, out var line))
            return [new LogSegment(raw, Flavour)];

        var message = line.Message;
        var prefix = raw[..(raw.Length - message.Length)];
        List<LogSegment> segments = [new(prefix, Time)];

        var parsed = EnglishGrammar.TryParse(message);
        List<(int Start, int Length, Brush Brush)> spans = [];
        switch (parsed)
        {
            case SwingEvent swing:
                AddSpan(spans, message, swing.Attacker is EnglishGrammar.You ? "YOU" : swing.Attacker, Attacker);
                if (swing.Ability != EnglishGrammar.AutoAttackAbility)
                    AddSpan(spans, message, swing.Ability, Ability);
                AddSpan(spans, message, swing.Victim is EnglishGrammar.You ? "YOU" : swing.Victim, Victim);
                if (swing.Damage.Number > 0)
                    AddSpan(spans, message, swing.Damage.Number.ToString("N0"), Amount);
                break;
            case DeathEvent death:
                AddSpan(spans, message, death.Victim is EnglishGrammar.You ? "You" : death.Victim, Death);
                if (death.Killer is not ("Unknown" or EnglishGrammar.You))
                    AddSpan(spans, message, death.Killer, Attacker);
                break;
            case null:
                segments.Add(new LogSegment(message, Flavour));
                return segments;
        }

        // Merge non-overlapping spans in order; gaps render as plain text.
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var cursor = 0;
        foreach (var (start, length, brush) in spans)
        {
            if (start < cursor)
                continue;
            if (start > cursor)
                segments.Add(new LogSegment(message[cursor..start], Plain));
            segments.Add(new LogSegment(message.Substring(start, length), brush));
            cursor = start + length;
        }
        if (cursor < message.Length)
            segments.Add(new LogSegment(message[cursor..], Plain));
        return segments;
    }

    private static void AddSpan(List<(int, int, Brush)> spans, string message, string token, Brush brush)
    {
        if (string.IsNullOrEmpty(token))
            return;
        var index = message.IndexOf(token, StringComparison.Ordinal);
        if (index >= 0)
            spans.Add((index, token.Length, brush));
    }
}
