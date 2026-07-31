using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.Views;

/// <summary>Attached property that fills a TextBlock's Inlines from a list
/// of coloured segments (WPF Inlines aren't directly bindable).</summary>
public static class SegmentedText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<LogSegment>), typeof(SegmentedText),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static void SetSegments(DependencyObject element, IReadOnlyList<LogSegment>? value) =>
        element.SetValue(SegmentsProperty, value);

    public static IReadOnlyList<LogSegment>? GetSegments(DependencyObject element) =>
        (IReadOnlyList<LogSegment>?)element.GetValue(SegmentsProperty);

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;
        textBlock.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<LogSegment> segments)
            return;
        foreach (var segment in segments)
            textBlock.Inlines.Add(new Run(segment.Text) { Foreground = segment.Brush });
    }
}
