using System.Windows;
using System.Windows.Input;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

/// <summary>
/// The shared drag-to-re-file behaviour for categorised lists (Triggers,
/// Timers): grip-initiated drags with a movement threshold, honest
/// highlighting (only targets where a drop would actually move something
/// light up; no-ops show the no-drop cursor), and highlight cleanup. The
/// owning view forwards its XAML events here and supplies the move action.
/// </summary>
public sealed class CategoryDragDrop(Action<ICategoryDropTarget, ICategoryDropTarget> move)
{
    private const string Format = "EQ2Parser.CategoryDrag";

    private ICategoryDropTarget? _dragCandidate;
    private Point _dragStart;
    private ICategoryDropTarget? _highlighted;

    private static ICategoryDropTarget? Context(object sender) =>
        (sender as FrameworkElement)?.DataContext as ICategoryDropTarget;

    private static ICategoryDropTarget? Dragged(DragEventArgs e) =>
        e.Data.GetData(Format) as ICategoryDropTarget;

    // ---- drag source (the ⠿ grip on item rows) ----

    public void GripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = Context(sender);
        _dragStart = e.GetPosition(null);
    }

    public void GripMouseUp() => _dragCandidate = null;

    public void GripMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var row = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(Format, row), DragDropEffects.Move);
        ClearHighlight();
    }

    // ---- drop targets (headers and rows alike) ----

    /// <summary>The category a drop on this element would file into, or null
    /// when it isn't a move (not a target / same category / onto itself).</summary>
    private static string? EffectiveTarget(ICategoryDropTarget? context, ICategoryDropTarget dragged) =>
        context is not null
        && !string.Equals(context.CategoryName, dragged.CategoryName, StringComparison.OrdinalIgnoreCase)
            ? context.CategoryName
            : null;

    public void DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var context = Context(sender);
        if (Dragged(e) is not { } dragged || EffectiveTarget(context, dragged) is null)
        {
            e.Effects = DragDropEffects.None;
            ClearHighlight();
            return;
        }
        e.Effects = DragDropEffects.Move;
        if (!ReferenceEquals(context, _highlighted))
        {
            ClearHighlight();
            context!.IsDropTarget = true;
            _highlighted = context;
        }
    }

    public void DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(Context(sender), _highlighted))
            ClearHighlight();
    }

    public void Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearHighlight();
        if (Dragged(e) is not { } dragged)
            return;
        var context = Context(sender);
        if (context is not null && EffectiveTarget(context, dragged) is not null)
            move(dragged, context);
    }

    private void ClearHighlight()
    {
        if (_highlighted is not null)
        {
            _highlighted.IsDropTarget = false;
            _highlighted = null;
        }
    }
}
