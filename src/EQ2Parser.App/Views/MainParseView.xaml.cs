using System.Windows;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class MainParseView : System.Windows.Controls.UserControl
{
    private ArchiveWindow? _archive;

    public MainParseView() => InitializeComponent();

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
