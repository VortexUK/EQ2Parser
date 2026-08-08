using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.ViewModels;

/// <summary>
/// The shared "Import XML file…" flow for the Triggers and Timers pages
/// (file picker → ActConfigImport → apply → summary line). The two pages
/// had byte-near-identical 40-line copies differing only in loc-key prefix
/// and summary emphasis.
/// </summary>
internal static class ActImportFlow
{
    /// <summary>Null = dialog cancelled. Applied is false for read/parse
    /// failures — the caller sets its ImportResult either way but only
    /// rebuilds rows when something landed.</summary>
    public sealed record Outcome(string Message, bool Applied);

    /// <param name="locPrefix">"TriggersVm_" or "TimersVm_" — selects the
    /// hosting page's strings.</param>
    /// <param name="timersFirst">Summary emphasis: the Timers page lists
    /// imported spell timers first, the Triggers page lists triggers first.</param>
    public static Outcome? ImportXmlFile(SourceManager manager, string locPrefix, bool timersFirst)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get(locPrefix + "ImportActXmlTitle"),
            Filter = Loc.Get(locPrefix + "XmlFileFilter"),
        };
        if (dialog.ShowDialog() != true)
            return null;
        string xml;
        try
        {
            xml = System.IO.File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return new Outcome(Loc.Format(locPrefix + "FileReadError", ex.Message), Applied: false);
        }
        var result = ActConfigImport.TryImport(xml);
        if (result is null)
            return new Outcome(Loc.Get(locPrefix + "NotActXml"), Applied: false);

        manager.SpellTimers.ImportMany(result.Timers);
        manager.Triggers.AddOrUpdateMany(result.Triggers);
        // AFTER the explicit spells land — the linked-timer default must not
        // shadow a real definition arriving in the same file.
        var linked = manager.SpellTimers.EnsureLinkedTimers(result.Triggers);

        List<string> parts = [];
        void AddTimers()
        {
            if (result.Timers.Count > 0)
                parts.Add(Loc.Format(
                    locPrefix + (result.Timers.Count == 1 ? "SpellTimersImportedOne" : "SpellTimersImportedMany"),
                    result.Timers.Count));
        }
        void AddTriggers()
        {
            if (result.Triggers.Count > 0)
                parts.Add(Loc.Format(
                    locPrefix + (result.Triggers.Count == 1 ? "TriggersImportedOne" : "TriggersImportedMany"),
                    result.Triggers.Count));
        }
        if (timersFirst)
        {
            AddTimers();
            AddTriggers();
        }
        else
        {
            AddTriggers();
            AddTimers();
        }
        if (linked > 0)
            parts.Add(Loc.Format(locPrefix + (linked == 1 ? "LinkedTimersOne" : "LinkedTimersMany"), linked));
        if (result.Skipped > 0)
            parts.Add(Loc.Format(locPrefix + (result.Skipped == 1 ? "EntriesSkippedOne" : "EntriesSkippedMany"), result.Skipped));
        return new Outcome(
            parts.Count > 0 ? string.Join(" · ", parts) : Loc.Get(locPrefix + "FileNoEntries"),
            Applied: true);
    }
}
