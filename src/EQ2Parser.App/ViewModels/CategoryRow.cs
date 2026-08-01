using CommunityToolkit.Mvvm.ComponentModel;

namespace EQ2Parser.App.ViewModels;

/// <summary>Anything a category drag-drop can land on: headers accept into
/// themselves, rows accept into their own category. Rows are also the drag
/// sources; IsDropTarget doubles as the hover highlight and landing flash.</summary>
public interface ICategoryDropTarget
{
    string CategoryName { get; }
    bool IsDropTarget { get; set; }
}

/// <summary>Master section divider in the trigger/timer trees — the
/// user's Custom set vs the read-only synced Lexicon set.</summary>
public sealed record SectionRow(string Title, string Subtitle = "");

/// <summary>Top-level zone header in the trigger/timer trees — grouping
/// only, deliberately NOT a drop target (drops land on mobs or their rows).</summary>
public sealed partial class ZoneRow(string name, bool expanded) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>Collapse-state key — distinct from Name when the same zone
    /// exists in both the Custom and Lexicon sections.</summary>
    public string Key { get; init; } = name;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private int _enabledCount;

    [ObservableProperty]
    private bool _isExpanded = expanded;
}

/// <summary>A collapsible category header in a categorised list (triggers,
/// timers) — also the drop target when dragging an item into the category.</summary>
public sealed partial class CategoryRow(string name, bool expanded) : ObservableObject, ICategoryDropTarget
{
    public string Name { get; } = name;
    public string CategoryName => Name;

    /// <summary>Optional parent-group marker (the timer tree stores the
    /// zone here) — lets composite trees key expansion and drops.</summary>
    public string? Tag { get; init; }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private int _enabledCount;

    [ObservableProperty]
    private bool _isExpanded = expanded;

    [ObservableProperty]
    private bool _isDropTarget;
}
