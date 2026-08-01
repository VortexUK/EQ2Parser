using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class CurationWindow
{
    public CurationWindow(SourceManager manager)
    {
        InitializeComponent();
        DataContext = new CurationViewModel(manager);
    }
}
