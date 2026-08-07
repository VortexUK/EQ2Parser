using System.Windows;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Views;

/// <summary>A trigger shared in chat: show what it does, offer Add/Ignore.
/// Non-modal — a raid share must never block the parser.</summary>
public partial class ShareOfferWindow : Window
{
    private readonly SourceManager _manager;
    private readonly SharedTrigger _share;

    public ShareOfferWindow(SourceManager manager, SharedTrigger share)
    {
        InitializeComponent();
        _manager = manager;
        _share = share;
        HeaderText.Text = Loc.Format("Share_Header", share.Sharer);
        RegexText.Text = share.Trigger.RegexText;
        SoundText.Text = share.Trigger.SoundType switch
        {
            TriggerSound.Tts => Loc.Format("Share_SoundTts", share.Trigger.SoundData),
            TriggerSound.WavFile => Loc.Format("Share_SoundWav", share.Trigger.SoundData),
            TriggerSound.Beep => Loc.Get("Share_SoundBeep"),
            _ => Loc.Get("Share_SoundNone"),
        };
        CategoryText.Text = share.Trigger.Category.Length > 0 ? share.Trigger.Category : "General";
        if (share.Trigger.StartsTimer && share.Trigger.TimerName.Length > 0)
        {
            TimerNote.Text = Loc.Format("Share_TimerNote", share.Trigger.TimerName);
            TimerNote.Visibility = Visibility.Visible;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _manager.Triggers.AddOrUpdate(_share.Trigger);
        _manager.AnnounceNotification(Loc.Format("Share_Added", _share.Sharer));
        Close();
    }

    private void Ignore_Click(object sender, RoutedEventArgs e) => Close();
}
