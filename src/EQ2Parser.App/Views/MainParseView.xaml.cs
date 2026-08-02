using System.Windows;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class MainParseView : System.Windows.Controls.UserControl
{
    public MainParseView()
    {
        InitializeComponent();
        // Loaded, not the ctor: the view is templated, so the DataContext
        // (and the persisted width) is only available once loaded.
        Loaded += (_, _) =>
        {
            if (DataContext is MainParseViewModel vm && vm.Manager.Settings.TreeColumnWidth is { } width)
                TreeColumn.Width = new GridLength(Math.Max(TreeColumn.MinWidth, width));
        };
    }

    private void ColumnsToggle_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // StaysOpen=False closes the popup on the mousedown, then the click
        // would re-toggle it straight back open — swallow the click so the
        // button is a real open/close toggle.
        if (ColumnsPopup.IsOpen)
        {
            ColumnsPopup.IsOpen = false;
            ColumnsToggle.IsChecked = false;
            e.Handled = true;
        }
    }

    private void DrillColumnsToggle_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DrillColumnsPopup.IsOpen)
        {
            DrillColumnsPopup.IsOpen = false;
            DrillColumnsToggle.IsChecked = false;
            e.Handled = true;
        }
    }

    private void SwingColumnsToggle_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SwingColumnsPopup.IsOpen)
        {
            SwingColumnsPopup.IsOpen = false;
            SwingColumnsToggle.IsChecked = false;
            e.Handled = true;
        }
    }

    private void TreeSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (DataContext is not MainParseViewModel vm)
            return;
        vm.Manager.Settings = vm.Manager.Settings with { TreeColumnWidth = TreeColumn.ActualWidth };
        vm.Manager.Settings.Save();
    }

    // Singleton-per-type via the live window list, NOT view fields — the
    // view is recreated on every tab switch, so fields forgot the open
    // window and a second click spawned a duplicate.

    private void Curation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainParseViewModel vm)
            return;
        if (FindOpen<CurationWindow>() is { } open)
        {
            open.Activate();
            return;
        }
        new CurationWindow(vm.Manager) { Owner = Window.GetWindow(this) }.Show();
    }

    private void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainParseViewModel vm)
            return;
        if (FindOpen<ArchiveWindow>() is { } open)
        {
            open.Activate();
            return;
        }
        new ArchiveWindow(vm.Manager) { Owner = Window.GetWindow(this) }.Show();
    }

    private static T? FindOpen<T>() where T : Window
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is T match && match.IsLoaded)
                return match;
        }
        return null;
    }
}
