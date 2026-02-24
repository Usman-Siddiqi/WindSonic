using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Navigation;
using WindSound.App.Interop;
using WindSound.App.Models;
using WindSound.App.ViewModels;

namespace WindSound.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        WindowGlassHelper.Apply(this);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestoreButton_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.PlaySelectedCommand.CanExecute(null))
        {
            _viewModel.PlaySelectedCommand.Execute(null);
        }
    }

    private async void SongPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpotifyTrack track })
        {
            await _viewModel.PlaySearchTrackAsync(track);
        }

        e.Handled = true;
    }

    private void SongQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpotifyTrack track })
        {
            _viewModel.QueueSearchTrack(track);
        }

        e.Handled = true;
    }

    private async void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is QueuedTrack queueItem)
        {
            await _viewModel.PlayQueueItemAsync(queueItem);
            e.Handled = true;
        }
    }

    private async void QueueRowPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueuedTrack queueItem })
        {
            await _viewModel.PlayQueueItemAsync(queueItem);
        }

        e.Handled = true;
    }

    private void QueueRowRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueuedTrack queueItem })
        {
            _viewModel.RemoveQueueItem(queueItem);
        }

        e.Handled = true;
    }

    private async void PlaylistTracksList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistTracksList.SelectedItem is SpotifyTrack track)
        {
            await _viewModel.PlayPlaylistTrackAsync(track);
            e.Handled = true;
        }
    }

    private async void PlaylistTrackPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpotifyTrack track })
        {
            await _viewModel.PlayPlaylistTrackAsync(track);
        }

        e.Handled = true;
    }

    private void PlaylistTrackQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpotifyTrack track })
        {
            _viewModel.QueuePlaylistTrack(track);
        }

        e.Handled = true;
    }

    private void PlaylistTrackRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpotifyTrack track })
        {
            _viewModel.RemovePlaylistTrack(track);
        }

        e.Handled = true;
    }

    private async void RecentsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentsList.SelectedItem is RecentTrackEntry recent)
        {
            await _viewModel.PlayRecentTrackAsync(recent);
            e.Handled = true;
        }
    }

    private async void RecentTrackPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentTrackEntry recent })
        {
            await _viewModel.PlayRecentTrackAsync(recent);
        }

        e.Handled = true;
    }

    private void RecentTrackQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentTrackEntry recent })
        {
            _viewModel.QueueRecentTrack(recent);
        }

        e.Handled = true;
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.BeginTimelineSeek();
    }

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            _viewModel.CommitTimelineSeek(slider.Value);
        }
        else
        {
            _viewModel.CancelTimelineSeek();
        }
    }

    private void SeekSlider_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is Slider slider)
        {
            _viewModel.CommitTimelineSeek(slider.Value);
        }
    }

    private void HelpLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
        }

        e.Handled = true;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focusedIsTextEntry = Keyboard.FocusedElement is TextBoxBase or PasswordBox;

        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchInput.Focus();
            SearchInput.SelectAll();
            e.Handled = true;
            return;
        }

        if (!focusedIsTextEntry && e.Key == Key.Space)
        {
            if (_viewModel.PauseResumeCommand.CanExecute(null))
            {
                _viewModel.PauseResumeCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (!focusedIsTextEntry && e.Key == Key.Delete)
        {
            if (QueueList.IsKeyboardFocusWithin && QueueList.SelectedItem is QueuedTrack queueItem)
            {
                _viewModel.RemoveQueueItem(queueItem);
                e.Handled = true;
                return;
            }

            if (PlaylistTracksList.IsKeyboardFocusWithin && PlaylistTracksList.SelectedItem is SpotifyTrack playlistTrack)
            {
                _viewModel.RemovePlaylistTrack(playlistTrack);
                e.Handled = true;
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _viewModel.Dispose();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
