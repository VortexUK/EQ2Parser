using System.Windows;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.Views;

/// <summary>First-run wizard: help a fresh install find its EQ2 log folder.
/// Auto-detected candidates (most recently played first) are listed;
/// Browse covers the rest; Skip is always available — the Sources page can
/// do everything later.</summary>
public partial class FirstRunWindow : Window
{
    private readonly SourceManager _manager;
    private string? _chosen;

    public FirstRunWindow(SourceManager manager)
    {
        _manager = manager;
        InitializeComponent();
        var candidates = Eq2LogLocator.FindLogFolders();
        if (candidates.Count > 0)
        {
            CandidateList.ItemsSource = candidates;
            CandidateList.SelectionChanged += (_, _) =>
            {
                if (CandidateList.SelectedItem is string path)
                    Choose(path);
            };
            CandidateList.SelectedIndex = 0; // best guess pre-selected
        }
        else
        {
            CandidateList.Visibility = Visibility.Collapsed;
            NoCandidatesText.Visibility = Visibility.Visible;
        }
    }

    private void Choose(string path)
    {
        _chosen = path;
        ChosenText.Text = path;
        StartButton.IsEnabled = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.Get("FirstRunVm_ChooseLogsFolderTitle"),
        };
        if (dialog.ShowDialog(this) == true)
        {
            CandidateList.SelectedItem = null;
            Choose(dialog.FolderName);
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_chosen is not null)
            _manager.AddWatchedFolder(_chosen);
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
