using System.Windows;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class MainParseView : System.Windows.Controls.UserControl
{
    private ArchiveWindow? _archive;
    private CurationWindow? _curation;

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

    private void Curation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainParseViewModel vm)
            return;
        if (_curation is { IsLoaded: true })
        {
            _curation.Activate();
            return;
        }
        _curation = new CurationWindow(vm.Manager) { Owner = Window.GetWindow(this) };
        _curation.Closed += (_, _) => _curation = null;
        _curation.Show();
    }

    private void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainParseViewModel vm)
            return;
        if (_archive is { IsLoaded: true })
        {
            _archive.Activate();
            return;
        }
        _archive = new ArchiveWindow(vm.Manager) { Owner = Window.GetWindow(this) };
        _archive.Closed += (_, _) => _archive = null;
        _archive.Show();
    }
}
