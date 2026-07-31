using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class TriggersView
{
    private TriggerRow? _dragCandidate;
    private Point _dragStart;
    private object? _highlighted;

    public TriggersView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TriggersViewModel oldVm)
                oldVm.TriggerMoved -= OnTriggerMoved;
            if (e.NewValue is TriggersViewModel newVm)
                newVm.TriggerMoved += OnTriggerMoved;
        };
    }

    /// <summary>After a drag-move: bring the row's new home on screen and
    /// flash it so the landing spot is unmistakable.</summary>
    private void OnTriggerMoved(TriggerRow row)
    {
        TriggerList.ScrollIntoView(row);
        row.IsDropTarget = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            row.IsDropTarget = false;
        };
        timer.Start();
    }

    // ---- drag source: the ⠿ grip on each trigger row ----

    private void DragGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = (sender as FrameworkElement)?.DataContext as TriggerRow;
        _dragStart = e.GetPosition(null);
    }

    private void DragGrip_MouseUp(object sender, MouseButtonEventArgs e) =>
        _dragCandidate = null;

    private void DragGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var row = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(TriggerRow), row), DragDropEffects.Move);
        ClearHighlight();
    }

    // ---- drop targets: category headers and trigger rows. Both highlight;
    //      a drop that would change nothing shows the no-drop cursor. ----

    private static TriggerRow? Dragged(DragEventArgs e) =>
        e.Data.GetData(typeof(TriggerRow)) as TriggerRow;

    /// <summary>The category a drop on this element would file into, or null
    /// when it isn't a move (not a target / same category / onto itself).</summary>
    private static string? EffectiveTarget(object? context, TriggerRow dragged)
    {
        var category = context switch
        {
            TriggerCategoryRow header => header.Name,
            TriggerRow row => row.Category,
            _ => null,
        };
        return category is not null
            && !string.Equals(category, dragged.Category, StringComparison.OrdinalIgnoreCase)
            ? category
            : null;
    }

    private static void SetHighlight(object? target, bool on)
    {
        switch (target)
        {
            case TriggerCategoryRow header:
                header.IsDropTarget = on;
                break;
            case TriggerRow row:
                row.IsDropTarget = on;
                break;
        }
    }

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var context = (sender as FrameworkElement)?.DataContext;
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
            SetHighlight(context, true);
            _highlighted = context;
        }
    }

    private void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals((sender as FrameworkElement)?.DataContext, _highlighted))
            ClearHighlight();
    }

    private void DropTarget_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearHighlight();
        if (Dragged(e) is not { } dragged || DataContext is not TriggersViewModel vm)
            return;
        var context = (sender as FrameworkElement)?.DataContext;
        if (EffectiveTarget(context, dragged) is { } category)
            vm.MoveTrigger(dragged, category);
    }

    private void ClearHighlight()
    {
        SetHighlight(_highlighted, false);
        _highlighted = null;
    }

    // ---- header click: expand/collapse ----

    private void CategoryHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TriggersViewModel vm
            && (sender as FrameworkElement)?.DataContext is TriggerCategoryRow row)
            vm.ToggleCategoryCommand.Execute(row);
    }
}
