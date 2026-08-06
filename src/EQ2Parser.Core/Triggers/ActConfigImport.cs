using System.Xml;

namespace EQ2Parser.Core.Triggers;

/// <summary>
/// Imports ACT's FULL settings export — <c>&lt;Config&gt;</c> with
/// <c>&lt;CustomTriggers&gt;</c> and <c>&lt;SpellTimers&gt;</c> sections,
/// hundreds of long-attribute <c>&lt;Trigger&gt;</c>/<c>&lt;Spell&gt;</c>
/// elements. Element parsing is delegated to
/// <see cref="ActShareFormat.TryImport"/> whose dual-dialect attribute
/// reader already accepts the long names, so the two importers can never
/// drift. Same XML hardening as the share parser (no DTD, no resolver).
/// </summary>
public static class ActConfigImport
{
    public sealed record Result(
        IReadOnlyList<Trigger> Triggers,
        IReadOnlyList<TimerDefinition> Timers,
        int Skipped);

    /// <summary>Parse a full ACT config document (or any XML containing
    /// Trigger/Spell elements). Null when the document itself is
    /// unparseable; individual bad elements are counted, not fatal.</summary>
    public static Result? TryImport(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 4096,
            });
            doc.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        List<Trigger> triggers = [];
        List<TimerDefinition> timers = [];
        var skipped = 0;
        foreach (var name in new[] { "Trigger", "Spell" })
        {
            foreach (XmlElement element in doc.GetElementsByTagName(name))
            {
                switch (ActShareFormat.TryImport(element.OuterXml))
                {
                    case Trigger trigger:
                        triggers.Add(trigger);
                        break;
                    case TimerDefinition timer:
                        timers.Add(timer);
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
        }
        if (triggers.Count == 0 && timers.Count == 0 && skipped == 0)
            return null; // no recognisable elements at all — not an ACT file
        return new Result(triggers, timers, skipped);
    }
}
