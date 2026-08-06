using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class TriggersView
{
    private readonly CategoryDragDrop _dragDrop;

    public TriggersView()
    {
        InitializeComponent();
        _dragDrop = new CategoryDragDrop((dragged, target) =>
        {
            if (DataContext is TriggersViewModel vm && dragged is TriggerRow row)
                vm.MoveTrigger(row, target);
        });
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
        FlashRow(row);
    }

    internal static void FlashRow(ICategoryDropTarget row)
    {
        row.IsDropTarget = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            row.IsDropTarget = false;
        };
        timer.Start();
    }

    // XAML event forwarders → the shared drag-drop behaviour.
    private void DragGrip_MouseDown(object sender, MouseButtonEventArgs e) => _dragDrop.GripMouseDown(sender, e);
    private void DragGrip_MouseUp(object sender, MouseButtonEventArgs e) => _dragDrop.GripMouseUp();
    private void DragGrip_MouseMove(object sender, MouseEventArgs e) => _dragDrop.GripMouseMove(sender, e);
    private void DropTarget_DragOver(object sender, DragEventArgs e) => _dragDrop.DragOver(sender, e);
    private void DropTarget_DragLeave(object sender, DragEventArgs e) => _dragDrop.DragLeave(sender, e);
    private void DropTarget_Drop(object sender, DragEventArgs e) => _dragDrop.Drop(sender, e);

    /// <summary>Click the trigger text → load it into the editor panel.</summary>
    private void TriggerBody_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TriggersViewModel vm
            && (sender as FrameworkElement)?.DataContext is TriggerRow row)
            vm.EditRowCommand.Execute(row);
    }

    private void CategoryHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TriggersViewModel vm
            && (sender as FrameworkElement)?.DataContext is CategoryRow row)
            vm.ToggleCategoryCommand.Execute(row);
    }

    private void ZoneHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TriggersViewModel vm
            && (sender as FrameworkElement)?.DataContext is ZoneRow row)
            vm.ToggleZoneCommand.Execute(row);
    }
}
