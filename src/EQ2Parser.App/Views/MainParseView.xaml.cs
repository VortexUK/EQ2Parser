using System.Windows;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class MainParseView : System.Windows.Controls.UserControl
{
    private ArchiveWindow? _archive;
    private CurationWindow? _curation;

    public MainParseView() => InitializeComponent();

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
