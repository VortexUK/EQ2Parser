using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class ArchiveWindow
{
    public ArchiveWindow(SourceManager manager)
    {
        InitializeComponent();
        DataContext = new ArchiveViewModel(manager);
    }
}
