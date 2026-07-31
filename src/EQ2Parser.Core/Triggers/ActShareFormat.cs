using System.Text;
using System.Xml;

namespace EQ2Parser.Core.Triggers;

/// <summary>
/// Import/export of ACT's XML share snippets — the compatibility promise
/// that lets 20 years of community triggers (and the EQ2Lexicon raids.db
/// trigger packs, which use the same format) work here unchanged.
///
/// Formats (docs/act-behavior.md §4):
///   &lt;Trigger R="..." SD="..." ST="n" CR="T|F" C="..." T="T|F" TN="..." Ta="T|F" /&gt;
///   &lt;Spell N="..." T="n" OM="T|F" R="T|F" A="T|F" WV="n" RD="T|F" M="T|F"
///          Tt="..." FC="argb" RV="n" C="..." RC="T|F" [SS="..." WS="..."] /&gt;
///
/// The regex attribute uses ACT's extra escaping on top of XML:
/// # → &amp;#35;, \\ → &amp;#92;&amp;#92;, \s → &amp;#92;s (applied on export,
/// reversed by standard XML entity decoding on import).
/// </summary>
public static class ActShareFormat
{
    /// <summary>Parse one share snippet. Returns a Trigger or a
    /// TimerDefinition (as object) depending on the element, or null for
    /// anything unrecognised/malformed.</summary>
    public static object? TryImport(string xml)
    {
        XmlDocument doc = new();
        try
        {
            doc.LoadXml(xml.Trim());
        }
        catch (XmlException)
        {
            return null;
        }
        var root = doc.DocumentElement;
        if (root is null)
            return null;
        return root.Name switch
        {
            "Trigger" => ImportTrigger(root),
            "Spell" => ImportSpell(root),
            _ => null,
        };
    }

    private static Trigger? ImportTrigger(XmlElement e)
    {
        // Two attribute dialects exist in the wild: the share-snippet short
        // names (R/SD/ST/CR/C/T/TN) and the config-file long names ACT uses
        // in its own settings XML and most community trigger packs
        // (Regex/SoundData/SoundType/CategoryRestrict/Category/Timer/
        // TimerName, plus Active which the share form doesn't carry).
        string Attr(string shortName, string longName) =>
            e.HasAttribute(shortName) ? e.GetAttribute(shortName) : e.GetAttribute(longName);
        static bool Flag(string value) =>
            value is "T" || value.Equals("True", StringComparison.OrdinalIgnoreCase);

        // Regex and Category are required — they form the identity key
        // (ACT rejects snippets without them the same way).
        var regex = Attr("R", "Regex");
        var category = Attr("C", "Category").Trim();
        if (regex.Length == 0 || category.Length == 0)
            return null;
        try
        {
            return new Trigger(regex, category)
            {
                // Share snippets carry no enabled state — default on.
                Enabled = !e.HasAttribute("Active") || Flag(e.GetAttribute("Active")),
                SoundType = int.TryParse(Attr("ST", "SoundType"), out var st) && Enum.IsDefined((TriggerSound)st)
                    ? (TriggerSound)st
                    : TriggerSound.None,
                SoundData = Attr("SD", "SoundData"),
                RestrictToCategoryZone = Flag(Attr("CR", "CategoryRestrict")),
                StartsTimer = Flag(Attr("T", "Timer")),
                TimerName = Attr("TN", "TimerName"),
            };
        }
        catch (ArgumentException)
        {
            return null; // invalid regex
        }
    }

    private static TimerDefinition? ImportSpell(XmlElement e)
    {
        // Same two dialects as triggers: share-snippet short names
        // (N/T/OM/R/A/WV/RD/M/Tt/FC/RV/C/RC/SS/WS) and the config-file
        // long names ACT's settings XML uses (Name/Timer/OnlyMasterTicks/
        // Restrict/Absolute/WarningValue/RadialDisplay/Modable/Tooltip/
        // FillColor/RemoveValue/Category/RestrictCategory/StartWav/
        // WarningWav, plus Checked which the share form doesn't carry).
        string Attr(string shortName, string longName) =>
            e.HasAttribute(shortName) ? e.GetAttribute(shortName) : e.GetAttribute(longName);
        static bool IsTrue(string value) =>
            value is "T" || value.Equals("True", StringComparison.OrdinalIgnoreCase);
        static bool IsFalse(string value) =>
            value is "F" || value.Equals("False", StringComparison.OrdinalIgnoreCase);
        static int IntOr(string value, int fallback) =>
            int.TryParse(value, out var v) ? v : fallback;

        var name = Attr("N", "Name");
        var category = Attr("C", "Category").Trim();
        if (name.Length == 0 || category.Length == 0)
            return null;
        return new TimerDefinition
        {
            Name = name,
            Category = category,
            Enabled = !e.HasAttribute("Checked") || IsTrue(e.GetAttribute("Checked")),
            DurationSeconds = IntOr(Attr("T", "Timer"), 30),
            WarningSeconds = IntOr(Attr("WV", "WarningValue"), 10),
            RemoveSeconds = IntOr(Attr("RV", "RemoveValue"), -15),
            AbsoluteTiming = IsTrue(Attr("A", "Absolute")),
            RestrictToMe = IsTrue(Attr("R", "Restrict")),
            RestrictToCategory = IsTrue(Attr("RC", "RestrictCategory")),
            OnlyMasterTicks = IsTrue(Attr("OM", "OnlyMasterTicks")),
            Modable = !IsFalse(Attr("M", "Modable")),
            RadialDisplay = !IsFalse(Attr("RD", "RadialDisplay")),
            FillColorArgb = IntOr(Attr("FC", "FillColor"), unchecked((int)0xFF0000FF)),
            Tooltip = Attr("Tt", "Tooltip"),
            StartSoundData = Attr("SS", "StartWav"),
            WarningSoundData = Attr("WS", "WarningWav"),
        };
    }

    public static string Export(Trigger t)
    {
        var sb = new StringBuilder("<Trigger");
        Attr(sb, "R", EscapeRegexForShare(t.RegexText));
        Attr(sb, "SD", Escape(t.SoundData));
        Attr(sb, "ST", ((int)t.SoundType).ToString());
        Attr(sb, "CR", t.RestrictToCategoryZone ? "T" : "F");
        Attr(sb, "C", Escape(t.Category));
        Attr(sb, "T", t.StartsTimer ? "T" : "F");
        Attr(sb, "TN", Escape(t.TimerName));
        Attr(sb, "Ta", "F");
        return sb.Append(" />").ToString();
    }

    public static string Export(TimerDefinition d)
    {
        var sb = new StringBuilder("<Spell");
        Attr(sb, "N", Escape(d.Name));
        Attr(sb, "T", d.DurationSeconds.ToString());
        Attr(sb, "OM", d.OnlyMasterTicks ? "T" : "F");
        Attr(sb, "R", d.RestrictToMe ? "T" : "F");
        Attr(sb, "A", d.AbsoluteTiming ? "T" : "F");
        Attr(sb, "WV", d.WarningSeconds.ToString());
        Attr(sb, "RD", d.RadialDisplay ? "T" : "F");
        Attr(sb, "M", d.Modable ? "T" : "F");
        Attr(sb, "Tt", Escape(d.Tooltip));
        Attr(sb, "FC", d.FillColorArgb.ToString());
        Attr(sb, "RV", d.RemoveSeconds.ToString());
        Attr(sb, "C", Escape(d.Category));
        Attr(sb, "RC", d.RestrictToCategory ? "T" : "F");
        if (d.StartSoundData.Length > 0) Attr(sb, "SS", Escape(d.StartSoundData));
        if (d.WarningSoundData.Length > 0) Attr(sb, "WS", Escape(d.WarningSoundData));
        return sb.Append(" />").ToString();
    }

    private static void Attr(StringBuilder sb, string name, string value) =>
        sb.Append(' ').Append(name).Append("=\"").Append(value).Append('"');

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>ACT's extra escaping layer for the regex attribute.</summary>
    private static string EscapeRegexForShare(string regex) => Escape(regex)
        .Replace("#", "&#35;")
        .Replace(@"\\", "&#92;&#92;")
        .Replace(@"\s", "&#92;s");
}
