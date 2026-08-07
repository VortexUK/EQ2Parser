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

    /// <summary>The Copy-for-Discord quick picker: rebuild the submenu just
    /// before the tree's context menu opens — "current settings" first,
    /// then every saved preset. Wired at the ListBox (a normal element);
    /// handlers on elements INSIDE the ItemContainerStyle's ContextMenu
    /// corrupt the XAML connector ids and crash at first item render.</summary>
    private void Tree_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (DataContext is not MainParseViewModel vm
            || sender is not System.Windows.Controls.ItemsControl list
            || e.OriginalSource is not DependencyObject origin)
            return;
        if (System.Windows.Controls.ItemsControl.ContainerFromElement(list, origin)
            is not FrameworkElement container
            || container.DataContext is not ParseNode node
            || container.ContextMenu is not { } menu)
            return;
        if (menu.Items.OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(m => m.Tag as string == "discord-picker") is not { } parent)
            return;

        parent.Items.Clear();
        var current = new System.Windows.Controls.MenuItem
        {
            Header = Localization.Loc.Get("Export_CurrentSettings"),
        };
        current.Click += (_, _) => vm.CopyDiscordCommand.Execute(node);
        parent.Items.Add(current);
        if (vm.ExportPresets.Count > 0)
            parent.Items.Add(new System.Windows.Controls.Separator());
        foreach (var preset in vm.ExportPresets)
        {
            // Double the underscores: MenuItem treats "_" as an access-key
            // marker and would swallow it from a user's preset name.
            var item = new System.Windows.Controls.MenuItem { Header = preset.Name.Replace("_", "__") };
            var name = preset.Name;
            item.Click += (_, _) => vm.CopyDiscordPreset(node, name);
            parent.Items.Add(item);
        }
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
