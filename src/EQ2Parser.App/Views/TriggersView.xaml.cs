using System.Windows;
using System.Windows.Input;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class TriggersView
{
    private TriggerRow? _dragCandidate;
    private Point _dragStart;
    private TriggerCategoryRow? _highlighted;

    public TriggersView()
    {
        InitializeComponent();
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

    // ---- drop targets: category headers (highlighted) and trigger rows
    //      (drop lands in that row's category) ----

    private static TriggerRow? Dragged(DragEventArgs e) =>
        e.Data.GetData(typeof(TriggerRow)) as TriggerRow;

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Dragged(e) is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        var header = (sender as FrameworkElement)?.DataContext as TriggerCategoryRow;
        if (!ReferenceEquals(header, _highlighted))
        {
            ClearHighlight();
            if (header is not null)
            {
                header.IsDropTarget = true;
                _highlighted = header;
            }
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
        var category = (sender as FrameworkElement)?.DataContext switch
        {
            TriggerCategoryRow header => header.Name,
            TriggerRow row => row.Category,
            _ => null,
        };
        if (category is not null)
            vm.MoveTrigger(dragged, category);
    }

    private void ClearHighlight()
    {
        if (_highlighted is not null)
        {
            _highlighted.IsDropTarget = false;
            _highlighted = null;
        }
    }

    // ---- header click: expand/collapse ----

    private void CategoryHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TriggersViewModel vm
            && (sender as FrameworkElement)?.DataContext is TriggerCategoryRow row)
            vm.ToggleCategoryCommand.Execute(row);
    }
}
