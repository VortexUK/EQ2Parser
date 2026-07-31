using System.Windows;
using System.Windows.Input;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class TimersView
{
    private readonly CategoryDragDrop _dragDrop;

    public TimersView()
    {
        InitializeComponent();
        _dragDrop = new CategoryDragDrop((dragged, category) =>
        {
            if (DataContext is TimersViewModel vm && dragged is TimerDefRow row)
                vm.MoveTimer(row, category);
        });
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TimersViewModel oldVm)
                oldVm.TimerMoved -= OnTimerMoved;
            if (e.NewValue is TimersViewModel newVm)
                newVm.TimerMoved += OnTimerMoved;
        };
    }

    private void OnTimerMoved(TimerDefRow row)
    {
        TimerList.ScrollIntoView(row);
        TriggersView.FlashRow(row);
    }

    // XAML event forwarders → the shared drag-drop behaviour.
    private void DragGrip_MouseDown(object sender, MouseButtonEventArgs e) => _dragDrop.GripMouseDown(sender, e);
    private void DragGrip_MouseUp(object sender, MouseButtonEventArgs e) => _dragDrop.GripMouseUp();
    private void DragGrip_MouseMove(object sender, MouseEventArgs e) => _dragDrop.GripMouseMove(sender, e);
    private void DropTarget_DragOver(object sender, DragEventArgs e) => _dragDrop.DragOver(sender, e);
    private void DropTarget_DragLeave(object sender, DragEventArgs e) => _dragDrop.DragLeave(sender, e);
    private void DropTarget_Drop(object sender, DragEventArgs e) => _dragDrop.Drop(sender, e);

    private void CategoryHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TimersViewModel vm
            && (sender as FrameworkElement)?.DataContext is CategoryRow row)
            vm.ToggleCategoryCommand.Execute(row);
    }
}
