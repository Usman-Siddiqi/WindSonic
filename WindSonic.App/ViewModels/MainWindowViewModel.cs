using System.Collections.ObjectModel;
using System.Collections.Specialized;
using WindSonic.App.Models;
using WindSonic.App.Services;
using WindSonic.App.Utils;

namespace WindSonic.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int MaxRecentTracks = 60;

    private readonly SettingsStore _settingsStore;
    private readonly SpotifyService _searchService;
    private readonly YouTubeAudioResolverService _youTubeAudioResolverService;
    private readonly NativeAudioPlayerService _audioPlayerService;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, YouTubeAudioSource> _resolvedSourceCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<YouTubeAudioSource>> _sourceOptionsCache = new(StringComparer.Ordinal);
    private readonly Random _random = new();

    private readonly AsyncRelayCommand _searchCommand;
    private readonly AsyncRelayCommand _playSelectedCommand;
    private readonly RelayCommand _queueSelectedCommand;
    private readonly RelayCommand _queueTopResultsCommand;
    private readonly RelayCommand _pauseResumeCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _previousTrackCommand;
    private readonly RelayCommand _nextTrackCommand;
    private readonly RelayCommand _toggleShuffleCommand;
    private readonly RelayCommand _cycleRepeatCommand;
    private readonly RelayCommand _clearQueueCommand;
    private readonly RelayCommand _shuffleQueueCommand;
    private readonly RelayCommand _surpriseMixCommand;
    private readonly RelayCommand _saveQueueAsPlaylistCommand;
    private readonly RelayCommand _createPlaylistCommand;
    private readonly RelayCommand _deletePlaylistCommand;
    private readonly RelayCommand _loadPlaylistToQueueCommand;
    private readonly RelayCommand _appendPlaylistToQueueCommand;
    private readonly RelayCommand _addSelectedResultToPlaylistCommand;
    private readonly RelayCommand _clearRecentsCommand;
    private readonly RelayCommand _playSelectedSourceCommand;
    private readonly RelayCommand _refreshSourceOptionsCommand;
    private readonly RelayCommand _saveCredentialsCommand;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _playCts;
    private bool _disposed;
    private bool _suspendPersistence;
    private bool _handlingPlaybackEnded;
    private DateTimeOffset _ignorePlaybackEndUntilUtc;

    private string _searchText = string.Empty;
    private SpotifyTrack? _selectedTrack;
    private QueuedTrack? _selectedQueueItem;
    private PlaylistDefinition? _selectedPlaylist;
    private SpotifyTrack? _selectedPlaylistTrack;
    private RecentTrackEntry? _selectedRecentTrack;
    private string _draftPlaylistName = string.Empty;
    private string _spotifyClientId = string.Empty;
    private string _spotifyClientSecret = string.Empty;
    private string _statusMessage = "Ready. Search songs (Apple iTunes metadata) and play via YouTube audio.";
    private string _nowPlayingTitle = "No track loaded";
    private string _nowPlayingMeta = "Search Apple/iTunes metadata and pick a result";
    private string _nowPlayingSource = "Audio-only YouTube playback via native libVLC";
    private YouTubeAudioSource? _selectedSourceOption;
    private string? _currentSourcePickerTrackId;
    private bool _isSearching;
    private bool _isResolving;
    private int _volume;
    private bool _shuffleEnabled;
    private RepeatMode _repeatMode = RepeatMode.All;
    private double _seekBarSeconds;
    private bool _isUserSeeking;
    private int _currentQueueIndex = -1;
    private PlayerSnapshot _playerSnapshot = new(false, false, false, TimeSpan.Zero, TimeSpan.Zero);

    private readonly List<int> _shuffleHistory = [];

    public MainWindowViewModel(
        SettingsStore settingsStore,
        SpotifyService spotifyService,
        YouTubeAudioResolverService youTubeAudioResolverService,
        NativeAudioPlayerService audioPlayerService)
    {
        _settingsStore = settingsStore;
        _searchService = spotifyService;
        _youTubeAudioResolverService = youTubeAudioResolverService;
        _audioPlayerService = audioPlayerService;

        SearchResults = new ObservableCollection<SpotifyTrack>();
        QueueItems = new ObservableCollection<QueuedTrack>();
        Playlists = new ObservableCollection<PlaylistDefinition>();
        SelectedPlaylistTracks = new ObservableCollection<SpotifyTrack>();
        RecentTracks = new ObservableCollection<RecentTrackEntry>();
        SourceOptions = new ObservableCollection<YouTubeAudioSource>();

        _settings = _settingsStore.Load();
        _spotifyClientId = _settings.SpotifyClientId;
        _spotifyClientSecret = _settings.SpotifyClientSecret;
        _volume = Math.Clamp(_settings.Volume, 0, 100);
        _shuffleEnabled = _settings.ShuffleEnabled;
        _repeatMode = Enum.IsDefined(typeof(RepeatMode), _settings.RepeatMode) ? _settings.RepeatMode : RepeatMode.All;

        _audioPlayerService.SnapshotChanged += OnPlayerSnapshotChanged;
        _audioPlayerService.PlaybackError += OnPlaybackError;
        _audioPlayerService.PlaybackEnded += OnPlaybackEnded;
        _audioPlayerService.Volume = _volume;

        QueueItems.CollectionChanged += OnQueueItemsCollectionChanged;
        Playlists.CollectionChanged += OnPlaylistsCollectionChanged;
        RecentTracks.CollectionChanged += OnRecentTracksCollectionChanged;

        LoadSettingsCollections();

        _searchCommand = new AsyncRelayCommand(SearchAsync, CanSearch);
        _playSelectedCommand = new AsyncRelayCommand(PlaySelectedAsync, CanPlaySelected);
        _queueSelectedCommand = new RelayCommand(QueueSelectedTrack, CanQueueSelectedTrack);
        _queueTopResultsCommand = new RelayCommand(QueueTopResults, CanQueueTopResults);
        _pauseResumeCommand = new RelayCommand(TogglePauseResume, CanPauseResume);
        _stopCommand = new RelayCommand(StopPlayback, CanStop);
        _previousTrackCommand = new RelayCommand(() => _ = PreviousTrackAsync(), CanPreviousTrack);
        _nextTrackCommand = new RelayCommand(() => _ = NextTrackAsync(), CanNextTrack);
        _toggleShuffleCommand = new RelayCommand(ToggleShuffle);
        _cycleRepeatCommand = new RelayCommand(CycleRepeat);
        _clearQueueCommand = new RelayCommand(ClearQueue, CanClearQueue);
        _shuffleQueueCommand = new RelayCommand(ShuffleQueueOrder, CanShuffleQueueOrder);
        _surpriseMixCommand = new RelayCommand(() => _ = SurpriseMixAsync(), CanSurpriseMix);
        _saveQueueAsPlaylistCommand = new RelayCommand(SaveQueueAsPlaylist, CanSaveQueueAsPlaylist);
        _createPlaylistCommand = new RelayCommand(CreatePlaylist);
        _deletePlaylistCommand = new RelayCommand(DeleteSelectedPlaylist, CanDeleteSelectedPlaylist);
        _loadPlaylistToQueueCommand = new RelayCommand(() => _ = LoadSelectedPlaylistToQueueAsync(), CanLoadSelectedPlaylist);
        _appendPlaylistToQueueCommand = new RelayCommand(AppendSelectedPlaylistToQueue, CanAppendSelectedPlaylist);
        _addSelectedResultToPlaylistCommand = new RelayCommand(AddSelectedResultToPlaylist, CanAddSelectedResultToPlaylist);
        _clearRecentsCommand = new RelayCommand(ClearRecents, () => RecentTracks.Count > 0);
        _playSelectedSourceCommand = new RelayCommand(() => _ = PlaySelectedSourceForCurrentTrackAsync(), CanPlaySelectedSource);
        _refreshSourceOptionsCommand = new RelayCommand(() => _ = RefreshSourceOptionsForCurrentTrackAsync(forceRefresh: true), CanRefreshSourceOptions);
        _saveCredentialsCommand = new RelayCommand(SaveCredentials);

        RefreshAllDerivedState();
        StatusMessage = "Ready. Search songs, queue them, and build playlists.";
    }

    public ObservableCollection<SpotifyTrack> SearchResults { get; }
    public ObservableCollection<QueuedTrack> QueueItems { get; }
    public ObservableCollection<PlaylistDefinition> Playlists { get; }
    public ObservableCollection<SpotifyTrack> SelectedPlaylistTracks { get; }
    public ObservableCollection<RecentTrackEntry> RecentTracks { get; }
    public ObservableCollection<YouTubeAudioSource> SourceOptions { get; }

    public AsyncRelayCommand SearchCommand => _searchCommand;
    public AsyncRelayCommand PlaySelectedCommand => _playSelectedCommand;
    public RelayCommand QueueSelectedCommand => _queueSelectedCommand;
    public RelayCommand QueueTopResultsCommand => _queueTopResultsCommand;
    public RelayCommand PauseResumeCommand => _pauseResumeCommand;
    public RelayCommand StopCommand => _stopCommand;
    public RelayCommand PreviousTrackCommand => _previousTrackCommand;
    public RelayCommand NextTrackCommand => _nextTrackCommand;
    public RelayCommand ToggleShuffleCommand => _toggleShuffleCommand;
    public RelayCommand CycleRepeatCommand => _cycleRepeatCommand;
    public RelayCommand ClearQueueCommand => _clearQueueCommand;
    public RelayCommand ShuffleQueueCommand => _shuffleQueueCommand;
    public RelayCommand SurpriseMixCommand => _surpriseMixCommand;
    public RelayCommand SaveQueueAsPlaylistCommand => _saveQueueAsPlaylistCommand;
    public RelayCommand CreatePlaylistCommand => _createPlaylistCommand;
    public RelayCommand DeletePlaylistCommand => _deletePlaylistCommand;
    public RelayCommand LoadPlaylistToQueueCommand => _loadPlaylistToQueueCommand;
    public RelayCommand AppendPlaylistToQueueCommand => _appendPlaylistToQueueCommand;
    public RelayCommand AddSelectedResultToPlaylistCommand => _addSelectedResultToPlaylistCommand;
    public RelayCommand ClearRecentsCommand => _clearRecentsCommand;
    public RelayCommand PlaySelectedSourceCommand => _playSelectedSourceCommand;
    public RelayCommand RefreshSourceOptionsCommand => _refreshSourceOptionsCommand;
    public RelayCommand SaveCredentialsCommand => _saveCredentialsCommand;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SearchSummary));
            }
        }
    }

    public SpotifyTrack? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (SetProperty(ref _selectedTrack, value))
            {
                OnPropertiesChanged(nameof(HasSelection), nameof(SelectionSubtitle));
                RefreshCommandStates();
            }
        }
    }

    public QueuedTrack? SelectedQueueItem
    {
        get => _selectedQueueItem;
        set
        {
            if (SetProperty(ref _selectedQueueItem, value))
            {
                OnPropertyChanged(nameof(QueueSelectionSubtitle));
                RefreshCommandStates();
            }
        }
    }

    public PlaylistDefinition? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            if (SetProperty(ref _selectedPlaylist, value))
            {
                RefreshSelectedPlaylistTracks();
                if (value is not null && string.IsNullOrWhiteSpace(_draftPlaylistName))
                {
                    DraftPlaylistName = value.Name;
                }

                OnPropertiesChanged(nameof(HasSelectedPlaylist), nameof(SelectedPlaylistSummary));
                PersistSettingsQuietly();
                RefreshCommandStates();
            }
        }
    }

    public SpotifyTrack? SelectedPlaylistTrack
    {
        get => _selectedPlaylistTrack;
        set
        {
            if (SetProperty(ref _selectedPlaylistTrack, value))
            {
                OnPropertyChanged(nameof(SelectedPlaylistTrackSubtitle));
            }
        }
    }

    public RecentTrackEntry? SelectedRecentTrack
    {
        get => _selectedRecentTrack;
        set => SetProperty(ref _selectedRecentTrack, value);
    }

    public string DraftPlaylistName
    {
        get => _draftPlaylistName;
        set
        {
            if (SetProperty(ref _draftPlaylistName, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public string SpotifyClientId
    {
        get => _spotifyClientId;
        set => SetProperty(ref _spotifyClientId, value);
    }

    public string SpotifyClientSecret
    {
        get => _spotifyClientSecret;
        set => SetProperty(ref _spotifyClientSecret, value);
    }

    public bool HasSpotifyCredentials => true;

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertiesChanged(nameof(IsBusy), nameof(SearchSummary));
                RefreshCommandStates();
            }
        }
    }

    public bool IsResolving
    {
        get => _isResolving;
        private set
        {
            if (SetProperty(ref _isResolving, value))
            {
                OnPropertiesChanged(nameof(IsBusy), nameof(ResolveSummary));
                RefreshCommandStates();
            }
        }
    }

    public bool IsBusy => IsSearching || IsResolving;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string SearchSummary =>
        IsSearching ? "Searching songs..." :
        SearchResults.Count == 0 ? (string.IsNullOrWhiteSpace(SearchText) ? "Search songs" : "No results yet") :
        $"{SearchResults.Count} result{(SearchResults.Count == 1 ? string.Empty : "s")}";

    public string ResolveSummary => IsResolving ? "Resolving YouTube audio stream..." : "Ready";
    public bool HasSelection => SelectedTrack is not null;
    public string SelectionSubtitle => SelectedTrack?.Subtitle ?? "Double-click a track to play it";
    public string QueueSelectionSubtitle => SelectedQueueItem?.Subtitle ?? "Select a queue item";
    public bool HasSelectedPlaylist => SelectedPlaylist is not null;
    public string SelectedPlaylistSummary => SelectedPlaylist is null ? "Pick a playlist" : $"{SelectedPlaylist.Name} • {(SelectedPlaylist.Tracks?.Count ?? 0)} tracks";
    public string SelectedPlaylistTrackSubtitle => SelectedPlaylistTrack?.Subtitle ?? "Select a playlist track";

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set => SetProperty(ref _nowPlayingTitle, value);
    }

    public string NowPlayingMeta
    {
        get => _nowPlayingMeta;
        private set => SetProperty(ref _nowPlayingMeta, value);
    }

    public string NowPlayingSource
    {
        get => _nowPlayingSource;
        private set => SetProperty(ref _nowPlayingSource, value);
    }

    public YouTubeAudioSource? SelectedSourceOption
    {
        get => _selectedSourceOption;
        set
        {
            if (SetProperty(ref _selectedSourceOption, value))
            {
                OnPropertyChanged(nameof(SourcePickerSummary));
                RefreshCommandStates();
            }
        }
    }

    public int Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _volume, clamped))
            {
                return;
            }

            _audioPlayerService.Volume = clamped;
            _settings.Volume = clamped;
            PersistSettingsQuietly();
        }
    }

    public bool ShuffleEnabled
    {
        get => _shuffleEnabled;
        set
        {
            if (!SetProperty(ref _shuffleEnabled, value))
            {
                return;
            }

            _settings.ShuffleEnabled = value;
            OnPropertiesChanged(nameof(ShuffleButtonText), nameof(QueueModeSummary), nameof(UpNextLabel));
            PersistSettingsQuietly();
        }
    }

    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        private set
        {
            if (!SetProperty(ref _repeatMode, value))
            {
                return;
            }

            _settings.RepeatMode = value;
            OnPropertiesChanged(nameof(RepeatButtonText), nameof(QueueModeSummary));
            PersistSettingsQuietly();
        }
    }

    public string ShuffleButtonText => ShuffleEnabled ? "Shuffle On" : "Shuffle Off";
    public string RepeatButtonText => RepeatMode switch { RepeatMode.One => "Repeat One", RepeatMode.None => "Repeat Off", _ => "Repeat All" };
    public string QueueModeSummary => $"{(ShuffleEnabled ? "Shuffle playback" : "In order")} • {RepeatButtonText}";

    public bool HasPlayback => _playerSnapshot.HasMedia;
    public bool IsPlaying => _playerSnapshot.IsPlaying;
    public bool IsPaused => _playerSnapshot.IsPaused;
    public string PlaybackStateText => !_playerSnapshot.HasMedia ? (_currentQueueIndex >= 0 ? "Ready" : "Idle") : _playerSnapshot.IsPlaying ? "Playing" : _playerSnapshot.IsPaused ? "Paused" : "Stopped";
    public string PlayPauseButtonText => _playerSnapshot.HasMedia ? (_playerSnapshot.IsPlaying ? "Pause" : "Resume") : (_currentQueueIndex >= 0 ? "Play" : "Resume");
    public double PlaybackPositionSeconds => Math.Max(0, _playerSnapshot.Position.TotalSeconds);
    public double PlaybackDurationSeconds => Math.Max(1, _playerSnapshot.Duration.TotalSeconds);
    public string PlaybackPositionLabel => FormatTime(_isUserSeeking ? TimeSpan.FromSeconds(SeekBarSeconds) : _playerSnapshot.Position);
    public string PlaybackDurationLabel => FormatTime(_playerSnapshot.Duration);

    public double SeekBarSeconds
    {
        get => _seekBarSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0d, Math.Max(1d, PlaybackDurationSeconds));
            if (SetProperty(ref _seekBarSeconds, clamped) && _isUserSeeking)
            {
                OnPropertyChanged(nameof(PlaybackPositionLabel));
            }
        }
    }

    public string QueueSummary => QueueItems.Count == 0 ? "Queue is empty" : $"{QueueItems.Count} tracks • {QueueDurationLabel}";
    public string QueueDurationLabel => FormatTime(QueueItems.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Track.Duration));
    public string QueuePositionLabel => _currentQueueIndex >= 0 && _currentQueueIndex < QueueItems.Count ? $"Queue {_currentQueueIndex + 1}/{QueueItems.Count}" : "Queue standby";
    public string UpNextLabel => BuildUpNextLabel();
    public string SourcePickerSummary => BuildSourcePickerSummary();
    public string PlaylistSummary => Playlists.Count == 0 ? "No playlists yet" : $"{Playlists.Count} playlists";
    public string RecentsSummary => RecentTracks.Count == 0 ? "No recent plays" : $"{RecentTracks.Count} recent tracks";
    public string EngineModeLabel => "Native WPF + libVLC (audio-only YouTube stream) • Metadata: Apple iTunes Search API";

    public async Task InitializeAsync()
    {
        await Task.CompletedTask;
        _audioPlayerService.Volume = Volume;
        OnPlayerSnapshotChanged(this, _audioPlayerService.CurrentSnapshot);
        UpdateNowPlayingFromQueuePointer();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _searchCts?.Cancel();
        _playCts?.Cancel();
        _searchCts?.Dispose();
        _playCts?.Dispose();

        _audioPlayerService.SnapshotChanged -= OnPlayerSnapshotChanged;
        _audioPlayerService.PlaybackError -= OnPlaybackError;
        _audioPlayerService.PlaybackEnded -= OnPlaybackEnded;
        QueueItems.CollectionChanged -= OnQueueItemsCollectionChanged;
        Playlists.CollectionChanged -= OnPlaylistsCollectionChanged;
        RecentTracks.CollectionChanged -= OnRecentTracksCollectionChanged;

        SaveCredentials();
    }

    public void BeginTimelineSeek() => _isUserSeeking = true;

    public void CommitTimelineSeek(double seconds)
    {
        if (!_playerSnapshot.HasMedia)
        {
            _isUserSeeking = false;
            return;
        }

        var clamped = Math.Clamp(seconds, 0d, Math.Max(0d, _playerSnapshot.Duration.TotalSeconds));
        SeekBarSeconds = clamped;
        _ = _audioPlayerService.Seek(TimeSpan.FromSeconds(clamped));
        _isUserSeeking = false;
    }

    public void CancelTimelineSeek()
    {
        _isUserSeeking = false;
        SeekBarSeconds = Math.Max(0, _playerSnapshot.Position.TotalSeconds);
    }

    public Task PlaySearchTrackAsync(SpotifyTrack track) => PlayAndQueueTrackAsync(track);
    public void QueueSearchTrack(SpotifyTrack track) => QueueTrack(track, playNow: false);
    public Task PlayQueueItemAsync(QueuedTrack queueItem) => PlayQueueItemCoreAsync(queueItem);
    public void RemoveQueueItem(QueuedTrack queueItem) => RemoveQueueItemCore(queueItem);
    public Task PlayPlaylistTrackAsync(SpotifyTrack track) => PlayAndQueueTrackAsync(track);
    public void QueuePlaylistTrack(SpotifyTrack track) => QueueTrack(track, playNow: false);
    public void RemovePlaylistTrack(SpotifyTrack track) => RemoveTrackFromSelectedPlaylist(track);
    public Task PlayRecentTrackAsync(RecentTrackEntry entry) => PlayAndQueueTrackAsync(entry.Track);
    public void QueueRecentTrack(RecentTrackEntry entry) => QueueTrack(entry.Track, playNow: false);

    private bool CanSearch() => !IsBusy && !string.IsNullOrWhiteSpace(SearchText);
    private bool CanPlaySelected() => !IsBusy && SelectedTrack is not null;
    private bool CanQueueSelectedTrack() => SelectedTrack is not null;
    private bool CanQueueTopResults() => SearchResults.Count > 0;
    private bool CanPauseResume() => HasPlayback || (!IsBusy && _currentQueueIndex >= 0 && _currentQueueIndex < QueueItems.Count);
    private bool CanStop() => HasPlayback;
    private bool CanPreviousTrack() => QueueItems.Count > 0 && !IsResolving;
    private bool CanNextTrack() => QueueItems.Count > 0 && !IsResolving;
    private bool CanClearQueue() => QueueItems.Count > 0;
    private bool CanShuffleQueueOrder() => QueueItems.Count > 1;
    private bool CanSurpriseMix() => SearchResults.Count > 0 || SelectedPlaylistTracks.Count > 0 || RecentTracks.Count > 0;
    private bool CanSaveQueueAsPlaylist() => QueueItems.Count > 0;
    private bool CanDeleteSelectedPlaylist() => SelectedPlaylist is not null;
    private bool CanLoadSelectedPlaylist() => SelectedPlaylist is not null && (SelectedPlaylist.Tracks?.Count ?? 0) > 0 && !IsResolving;
    private bool CanAppendSelectedPlaylist() => SelectedPlaylist is not null && (SelectedPlaylist.Tracks?.Count ?? 0) > 0;
    private bool CanAddSelectedResultToPlaylist() => SelectedTrack is not null && SelectedPlaylist is not null;
    private bool CanPlaySelectedSource() => !IsResolving && SelectedSourceOption is not null && _currentQueueIndex >= 0 && _currentQueueIndex < QueueItems.Count;
    private bool CanRefreshSourceOptions() => !IsResolving && _currentQueueIndex >= 0 && _currentQueueIndex < QueueItems.Count;

    private async Task SearchAsync()
    {
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        IsSearching = true;
        StatusMessage = $"Searching songs for \"{query}\"...";

        try
        {
            var results = await _searchService.SearchTracksAsync(query, SpotifyClientId, SpotifyClientSecret, 40, _searchCts.Token);
            SearchResults.Clear();
            foreach (var track in results)
            {
                SearchResults.Add(track);
            }

            SelectedTrack = SearchResults.FirstOrDefault();
            StatusMessage = SearchResults.Count > 0
                ? "Search finished. Double-click a result to play, or queue tracks to build a session."
                : "No songs matched that search.";
            OnPropertyChanged(nameof(SearchSummary));
            RefreshCommandStates();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Search cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task PlaySelectedAsync()
    {
        if (SelectedTrack is null)
        {
            return;
        }

        await PlayAndQueueTrackAsync(SelectedTrack);
    }

    private void QueueSelectedTrack()
    {
        if (SelectedTrack is null)
        {
            return;
        }

        QueueTrack(SelectedTrack, playNow: false);
    }

    private void QueueTopResults()
    {
        if (SearchResults.Count == 0)
        {
            return;
        }

        var count = Math.Min(10, SearchResults.Count);
        _suspendPersistence = true;
        try
        {
            for (var i = 0; i < count; i++)
            {
                InsertTrackAtEnd(SearchResults[i]);
            }
        }
        finally
        {
            _suspendPersistence = false;
        }

        StatusMessage = $"Queued top {count} search result{(count == 1 ? string.Empty : "s")}.";
        RefreshAllDerivedState();
        PersistSettingsQuietly();
    }

    private void TogglePauseResume()
    {
        if (!_playerSnapshot.HasMedia && _currentQueueIndex >= 0 && _currentQueueIndex < QueueItems.Count)
        {
            _ = PlayQueueIndexAsync(_currentQueueIndex, userInitiated: true, addToHistory: true);
            return;
        }

        _audioPlayerService.TogglePauseResume();
    }

    private void StopPlayback()
    {
        _ignorePlaybackEndUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(900);
        _audioPlayerService.Stop();
        _isUserSeeking = false;
        SeekBarSeconds = 0;
        StatusMessage = "Playback stopped.";
    }

    private async Task PreviousTrackAsync()
    {
        if (_currentQueueIndex < 0)
        {
            if (QueueItems.Count > 0)
            {
                await PlayQueueIndexAsync(0, userInitiated: true, addToHistory: true);
            }

            return;
        }

        if (_playerSnapshot.HasMedia && _playerSnapshot.Position >= TimeSpan.FromSeconds(4))
        {
            _ = _audioPlayerService.Seek(TimeSpan.Zero);
            StatusMessage = "Restarted current track.";
            return;
        }

        if (ShuffleEnabled && _shuffleHistory.Count >= 2)
        {
            _shuffleHistory.RemoveAt(_shuffleHistory.Count - 1);
            var previous = _shuffleHistory[^1];
            if (previous >= 0 && previous < QueueItems.Count)
            {
                await PlayQueueIndexAsync(previous, userInitiated: true, addToHistory: false);
                return;
            }
        }

        var previousIndex = _currentQueueIndex - 1;
        if (previousIndex < 0)
        {
            previousIndex = RepeatMode == RepeatMode.All && QueueItems.Count > 0 ? QueueItems.Count - 1 : -1;
        }

        if (previousIndex >= 0)
        {
            await PlayQueueIndexAsync(previousIndex, userInitiated: true, addToHistory: true);
        }
    }

    private async Task NextTrackAsync()
    {
        var nextIndex = GetNextQueueIndex(manualAdvance: true);
        if (nextIndex < 0)
        {
            StatusMessage = "No next track in queue.";
            return;
        }

        await PlayQueueIndexAsync(nextIndex, userInitiated: true, addToHistory: true);
    }

    private void ToggleShuffle()
    {
        ShuffleEnabled = !ShuffleEnabled;
        StatusMessage = ShuffleEnabled ? "Shuffle playback enabled." : "Shuffle playback disabled.";
        RefreshAllDerivedState();
    }

    private void CycleRepeat()
    {
        RepeatMode = RepeatMode switch
        {
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.None,
            _ => RepeatMode.All
        };

        StatusMessage = RepeatButtonText;
        RefreshAllDerivedState();
    }

    private void ClearQueue()
    {
        _ignorePlaybackEndUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(900);
        _audioPlayerService.Stop();

        _suspendPersistence = true;
        try
        {
            QueueItems.Clear();
            _currentQueueIndex = -1;
            SelectedQueueItem = null;
            _shuffleHistory.Clear();
        }
        finally
        {
            _suspendPersistence = false;
        }

        UpdateNowPlayingFromQueuePointer(clearIfNone: true);
        StatusMessage = "Queue cleared.";
        RefreshAllDerivedState();
        PersistSettingsQuietly();
    }

    private void ShuffleQueueOrder()
    {
        if (QueueItems.Count <= 1)
        {
            return;
        }

        var current = _currentQueueIndex >= 0 && _currentQueueIndex < QueueItems.Count ? QueueItems[_currentQueueIndex] : null;
        var items = QueueItems.ToList();
        if (current is not null)
        {
            items.Remove(current);
        }

        ShuffleList(items);

        _suspendPersistence = true;
        try
        {
            QueueItems.Clear();
            if (current is not null)
            {
                QueueItems.Add(current);
            }

            foreach (var item in items)
            {
                QueueItems.Add(item);
            }

            _currentQueueIndex = current is null ? -1 : 0;
            SelectedQueueItem = current ?? QueueItems.FirstOrDefault();
            _shuffleHistory.Clear();
        }
        finally
        {
            _suspendPersistence = false;
        }

        StatusMessage = "Queue order shuffled (current track pinned).";
        RefreshAllDerivedState();
        PersistSettingsQuietly();
    }

    private async Task SurpriseMixAsync()
    {
        var pool = new List<SpotifyTrack>();
        pool.AddRange(SearchResults.Take(25));
        pool.AddRange(SelectedPlaylistTracks.Take(25));
        pool.AddRange(RecentTracks.Take(25).Select(static x => x.Track));

        var deduped = pool
            .GroupBy(static t => $"{t.Id}|{t.Title}|{t.Artists}", StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .ToList();

        if (deduped.Count == 0)
        {
            StatusMessage = "Search or play a few tracks first so WindSonic can build a surprise mix.";
            return;
        }

        ShuffleList(deduped);
        var mix = deduped.Take(Math.Min(20, deduped.Count)).ToList();
        await ReplaceQueueAndPlayAsync(mix, "Surprise mix loaded.");
    }

    private void SaveQueueAsPlaylist()
    {
        if (QueueItems.Count == 0)
        {
            return;
        }

        var playlist = new PlaylistDefinition
        {
            Name = string.IsNullOrWhiteSpace(DraftPlaylistName) ? $"Queue Mix {DateTime.Now:MMM d HH:mm}" : DraftPlaylistName.Trim(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Tracks = QueueItems.Select(static q => CloneTrack(q.Track)).ToList()
        };

        Playlists.Insert(0, playlist);
        SelectedPlaylist = playlist;
        DraftPlaylistName = playlist.Name;
        StatusMessage = $"Saved queue as playlist: {playlist.Name}";
        PersistSettingsQuietly();
        RefreshAllDerivedState();
    }

    private void CreatePlaylist()
    {
        var playlist = new PlaylistDefinition
        {
            Name = string.IsNullOrWhiteSpace(DraftPlaylistName) ? GeneratePlaylistName() : DraftPlaylistName.Trim(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Tracks = []
        };

        Playlists.Insert(0, playlist);
        SelectedPlaylist = playlist;
        DraftPlaylistName = playlist.Name;
        StatusMessage = $"Created playlist: {playlist.Name}";
        PersistSettingsQuietly();
        RefreshAllDerivedState();
    }

    private void DeleteSelectedPlaylist()
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        var name = SelectedPlaylist.Name;
        Playlists.Remove(SelectedPlaylist);
        SelectedPlaylist = Playlists.FirstOrDefault();
        if (SelectedPlaylist is null)
        {
            SelectedPlaylistTrack = null;
            if (string.Equals(DraftPlaylistName, name, StringComparison.Ordinal))
            {
                DraftPlaylistName = string.Empty;
            }
        }

        StatusMessage = $"Deleted playlist: {name}";
        PersistSettingsQuietly();
        RefreshAllDerivedState();
    }

    private async Task LoadSelectedPlaylistToQueueAsync()
    {
        var tracks = SelectedPlaylist?.Tracks ?? [];
        if (SelectedPlaylist is null || tracks.Count == 0)
        {
            return;
        }

        await ReplaceQueueAndPlayAsync(tracks, $"Loaded playlist: {SelectedPlaylist.Name}");
    }

    private void AppendSelectedPlaylistToQueue()
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        var tracks = SelectedPlaylist.Tracks ??= [];
        if (tracks.Count == 0)
        {
            return;
        }

        _suspendPersistence = true;
        try
        {
            foreach (var track in tracks)
            {
                InsertTrackAtEnd(track);
            }
        }
        finally
        {
            _suspendPersistence = false;
        }

        StatusMessage = $"Appended {tracks.Count} tracks from {SelectedPlaylist.Name}.";
        RefreshAllDerivedState();
        PersistSettingsQuietly();
    }

    private void AddSelectedResultToPlaylist()
    {
        if (SelectedTrack is null || SelectedPlaylist is null)
        {
            return;
        }

        var selectedTrack = SelectedTrack;
        var selectedPlaylist = SelectedPlaylist;
        var playlistName = selectedPlaylist.Name;
        var trackTitle = selectedTrack.Title;

        (selectedPlaylist.Tracks ??= []).Add(CloneTrack(selectedTrack));
        selectedPlaylist.UpdatedAtUtc = DateTimeOffset.UtcNow;
        RefreshSelectedPlaylistTracks();
        NudgePlaylistRefresh(selectedPlaylist);
        StatusMessage = $"Added to {playlistName}: {trackTitle}";
        PersistSettingsQuietly();
        RefreshAllDerivedState();
    }

    private void ClearRecents()
    {
        RecentTracks.Clear();
        StatusMessage = "Recent plays cleared.";
        PersistSettingsQuietly();
        RefreshAllDerivedState();
    }

    private void SaveCredentials()
    {
        _settings.SpotifyClientId = SpotifyClientId.Trim();
        _settings.SpotifyClientSecret = SpotifyClientSecret.Trim();
        _settings.Volume = Volume;
        _settings.ShuffleEnabled = ShuffleEnabled;
        _settings.RepeatMode = RepeatMode;
        SyncSettingsCollections();

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save settings: {ex.Message}";
        }
    }

    private async Task PlayAndQueueTrackAsync(SpotifyTrack track)
    {
        var playIndex = InsertTrackAfterCurrent(track);
        await PlayQueueIndexAsync(playIndex, userInitiated: true, addToHistory: true);
    }

    private void QueueTrack(SpotifyTrack track, bool playNow)
    {
        var index = InsertTrackAtEnd(track);
        SelectedQueueItem = QueueItems[index];
        StatusMessage = $"Queued: {track.Title}";
        RefreshAllDerivedState();

        if (playNow)
        {
            _ = PlayQueueIndexAsync(index, userInitiated: true, addToHistory: true);
        }
    }

    private async Task PlayQueueItemCoreAsync(QueuedTrack queueItem)
    {
        var index = QueueItems.IndexOf(queueItem);
        if (index >= 0)
        {
            await PlayQueueIndexAsync(index, userInitiated: true, addToHistory: true);
        }
    }

    private void RemoveQueueItemCore(QueuedTrack queueItem)
    {
        var index = QueueItems.IndexOf(queueItem);
        if (index < 0)
        {
            return;
        }

        var wasCurrent = index == _currentQueueIndex;
        var removed = QueueItems[index];
        QueueItems.RemoveAt(index);

        if (_currentQueueIndex > index)
        {
            _currentQueueIndex--;
        }
        else if (wasCurrent)
        {
            if (QueueItems.Count == 0)
            {
                _currentQueueIndex = -1;
            }
            else if (index >= QueueItems.Count)
            {
                _currentQueueIndex = QueueItems.Count - 1;
            }
            else
            {
                _currentQueueIndex = index;
            }
        }

        SelectedQueueItem = _currentQueueIndex >= 0 && _currentQueueIndex < QueueItems.Count ? QueueItems[_currentQueueIndex] : QueueItems.FirstOrDefault();
        _shuffleHistory.Clear();
        UpdateNowPlayingFromQueuePointer();
        StatusMessage = $"Removed from queue: {removed.Track.Title}";
        RefreshAllDerivedState();
        PersistSettingsQuietly();
    }

    private void RemoveTrackFromSelectedPlaylist(SpotifyTrack track)
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        var selectedPlaylist = SelectedPlaylist;
        var playlistName = selectedPlaylist.Name;
        var trackTitle = track.Title;
        var playlistTracks = selectedPlaylist.Tracks ??= [];
        if (!playlistTracks.Remove(track))
        {
            return;
        }

        selectedPlaylist.UpdatedAtUtc = DateTimeOffset.UtcNow;
        RefreshSelectedPlaylistTracks();
        NudgePlaylistRefresh(selectedPlaylist);
        StatusMessage = $"Removed from {playlistName}: {trackTitle}";
        PersistSettingsQuietly();
        RefreshAllDerivedState();
    }

    private async Task ReplaceQueueAndPlayAsync(IEnumerable<SpotifyTrack> tracks, string statusPrefix)
    {
        var list = tracks.Select(CloneTrack).ToList();
        if (list.Count == 0)
        {
            return;
        }

        _ignorePlaybackEndUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(900);
        if (HasPlayback)
        {
            _audioPlayerService.Stop();
        }

        _suspendPersistence = true;
        try
        {
            QueueItems.Clear();
            foreach (var track in list)
            {
                QueueItems.Add(new QueuedTrack
                {
                    QueueItemId = Guid.NewGuid().ToString("N"),
                    Track = track,
                    AddedAtUtc = DateTimeOffset.UtcNow
                });
            }

            _currentQueueIndex = -1;
            _shuffleHistory.Clear();
            SelectedQueueItem = QueueItems.FirstOrDefault();
        }
        finally
        {
            _suspendPersistence = false;
        }

        StatusMessage = $"{statusPrefix} {list.Count} tracks.";
        RefreshAllDerivedState();
        PersistSettingsQuietly();
        await PlayQueueIndexAsync(0, userInitiated: true, addToHistory: true);
    }

    private async Task PlayQueueIndexAsync(int index, bool userInitiated, bool addToHistory, YouTubeAudioSource? forcedSource = null)
    {
        if (index < 0 || index >= QueueItems.Count)
        {
            return;
        }

        var item = QueueItems[index];
        var track = item.Track;

        _playCts?.Cancel();
        _playCts?.Dispose();
        _playCts = new CancellationTokenSource();

        _currentQueueIndex = index;
        SelectedQueueItem = item;
        UpdateNowPlayingFromQueuePointer();
        NowPlayingSource = "Resolving YouTube source...";
        IsResolving = true;
        StatusMessage = $"Resolving YouTube audio for {track.SearchHint}...";
        RefreshAllDerivedState();

        try
        {
            YouTubeAudioSource source;

            if (forcedSource is not null)
            {
                source = forcedSource;
                _resolvedSourceCache[track.Id] = forcedSource;
                if (!_sourceOptionsCache.TryGetValue(track.Id, out var existingOptions) || existingOptions.Count == 0)
                {
                    existingOptions = [forcedSource];
                    _sourceOptionsCache[track.Id] = existingOptions;
                }

                ApplySourceOptions(track, existingOptions, forcedSource.VideoId);
            }
            else if (_sourceOptionsCache.TryGetValue(track.Id, out var cachedOptions) && cachedOptions.Count > 0)
            {
                source = _resolvedSourceCache.TryGetValue(track.Id, out var cachedPreferred) &&
                         cachedOptions.Any(x => string.Equals(x.VideoId, cachedPreferred.VideoId, StringComparison.Ordinal))
                    ? cachedPreferred
                    : cachedOptions[0];

                ApplySourceOptions(track, cachedOptions, source.VideoId);
                _resolvedSourceCache[track.Id] = source;
            }
            else
            {
                var candidates = await _youTubeAudioResolverService.ResolveCandidatesForTrackAsync(track, 5, _playCts.Token).ConfigureAwait(true);
                source = candidates.First();
                _sourceOptionsCache[track.Id] = candidates;
                _resolvedSourceCache[track.Id] = source;
                ApplySourceOptions(track, candidates, source.VideoId);
            }

            _ignorePlaybackEndUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(1200);
            _audioPlayerService.PlayStream(source.StreamUrl, source.Duration ?? track.Duration);
            _audioPlayerService.Volume = Volume;

            if (addToHistory)
            {
                if (_shuffleHistory.Count == 0 || _shuffleHistory[^1] != index)
                {
                    _shuffleHistory.Add(index);
                }
            }

            AddRecent(track);
            NowPlayingSource = $"YouTube: {source.Title}  •  {source.Channel}";
            StatusMessage = userInitiated ? $"Playing: {track.Title}" : $"Auto-playing: {track.Title}";
            PersistSettingsQuietly();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Playback request cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsResolving = false;
        }
    }

    private async Task PlaySelectedSourceForCurrentTrackAsync()
    {
        if (_currentQueueIndex < 0 || _currentQueueIndex >= QueueItems.Count || SelectedSourceOption is null)
        {
            return;
        }

        var track = QueueItems[_currentQueueIndex].Track;
        await PlayQueueIndexAsync(_currentQueueIndex, userInitiated: true, addToHistory: false, forcedSource: SelectedSourceOption);
        StatusMessage = $"Switched source: {SelectedSourceOption.Channel}";
        _resolvedSourceCache[track.Id] = SelectedSourceOption;
        PersistSettingsQuietly();
    }

    private async Task RefreshSourceOptionsForCurrentTrackAsync(bool forceRefresh)
    {
        if (_currentQueueIndex < 0 || _currentQueueIndex >= QueueItems.Count)
        {
            return;
        }

        var track = QueueItems[_currentQueueIndex].Track;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            StatusMessage = forceRefresh ? "Refreshing YouTube source options..." : StatusMessage;
            var options = await EnsureSourceOptionsLoadedAsync(track, cts.Token, forceRefresh).ConfigureAwait(true);
            var preferredVideoId = _resolvedSourceCache.TryGetValue(track.Id, out var preferred) ? preferred.VideoId : options.FirstOrDefault()?.VideoId;
            ApplySourceOptions(track, options, preferredVideoId);

            if (forceRefresh)
            {
                StatusMessage = $"Found {options.Count} YouTube source option{(options.Count == 1 ? string.Empty : "s")} for {track.Title}.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Refreshing sources timed out.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task<IReadOnlyList<YouTubeAudioSource>> EnsureSourceOptionsLoadedAsync(SpotifyTrack track, CancellationToken cancellationToken, bool forceRefresh)
    {
        if (!forceRefresh && _sourceOptionsCache.TryGetValue(track.Id, out var cached) && cached.Count > 0)
        {
            return cached;
        }

        var options = await _youTubeAudioResolverService.ResolveCandidatesForTrackAsync(track, 5, cancellationToken).ConfigureAwait(true);
        _sourceOptionsCache[track.Id] = options;
        return options;
    }

    private void ApplySourceOptions(SpotifyTrack track, IReadOnlyList<YouTubeAudioSource> options, string? preferredVideoId)
    {
        _currentSourcePickerTrackId = track.Id;

        SourceOptions.Clear();
        foreach (var option in options)
        {
            SourceOptions.Add(option);
        }

        SelectedSourceOption = SourceOptions.FirstOrDefault(x =>
                                   !string.IsNullOrWhiteSpace(preferredVideoId) &&
                                   string.Equals(x.VideoId, preferredVideoId, StringComparison.Ordinal))
                               ?? SourceOptions.FirstOrDefault();

        OnPropertyChanged(nameof(SourcePickerSummary));
        RefreshCommandStates();
    }

    private void SelectSourceOptionForTrack(string trackId, string videoId)
    {
        if (!string.Equals(_currentSourcePickerTrackId, trackId, StringComparison.Ordinal))
        {
            return;
        }

        SelectedSourceOption = SourceOptions.FirstOrDefault(x => string.Equals(x.VideoId, videoId, StringComparison.Ordinal))
                             ?? SelectedSourceOption;
    }

    private async void OnPlaybackEnded(object? sender, EventArgs e)
    {
        if (_disposed || _handlingPlaybackEnded || DateTimeOffset.UtcNow < _ignorePlaybackEndUntilUtc)
        {
            return;
        }

        _handlingPlaybackEnded = true;
        try
        {
            if (_currentQueueIndex < 0 || _currentQueueIndex >= QueueItems.Count)
            {
                return;
            }

            if (RepeatMode == RepeatMode.One)
            {
                await PlayQueueIndexAsync(_currentQueueIndex, userInitiated: false, addToHistory: false);
                return;
            }

            var nextIndex = GetNextQueueIndex(manualAdvance: false);
            if (nextIndex < 0)
            {
                StatusMessage = "Queue finished.";
                return;
            }

            await PlayQueueIndexAsync(nextIndex, userInitiated: false, addToHistory: true);
        }
        finally
        {
            _handlingPlaybackEnded = false;
        }
    }

    private void OnPlaybackError(object? sender, string message)
    {
        StatusMessage = message;
    }

    private void OnPlayerSnapshotChanged(object? sender, PlayerSnapshot snapshot)
    {
        _playerSnapshot = snapshot;
        if (!_isUserSeeking)
        {
            SeekBarSeconds = Math.Max(0, snapshot.Position.TotalSeconds);
        }

        OnPropertiesChanged(
            nameof(HasPlayback), nameof(IsPlaying), nameof(IsPaused), nameof(PlaybackStateText),
            nameof(PlayPauseButtonText), nameof(PlaybackPositionSeconds), nameof(PlaybackDurationSeconds),
            nameof(PlaybackPositionLabel), nameof(PlaybackDurationLabel), nameof(SeekBarSeconds));

        RefreshCommandStates();
    }

    private void OnQueueItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_currentQueueIndex >= QueueItems.Count)
        {
            _currentQueueIndex = QueueItems.Count - 1;
        }

        OnPropertiesChanged(nameof(QueueSummary), nameof(QueueDurationLabel), nameof(QueuePositionLabel), nameof(UpNextLabel));
        RefreshCommandStates();
        if (!_suspendPersistence)
        {
            PersistSettingsQuietly();
        }
    }

    private void OnPlaylistsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        OnPropertyChanged(nameof(PlaylistSummary));
        RefreshCommandStates();
        if (!_suspendPersistence)
        {
            PersistSettingsQuietly();
        }
    }

    private void OnRecentTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        OnPropertyChanged(nameof(RecentsSummary));
        _clearRecentsCommand?.RaiseCanExecuteChanged();
        if (!_suspendPersistence)
        {
            PersistSettingsQuietly();
        }
    }

    private int InsertTrackAtEnd(SpotifyTrack track)
    {
        var item = new QueuedTrack
        {
            QueueItemId = Guid.NewGuid().ToString("N"),
            Track = CloneTrack(track),
            AddedAtUtc = DateTimeOffset.UtcNow
        };
        QueueItems.Add(item);
        return QueueItems.Count - 1;
    }

    private int InsertTrackAfterCurrent(SpotifyTrack track)
    {
        var item = new QueuedTrack
        {
            QueueItemId = Guid.NewGuid().ToString("N"),
            Track = CloneTrack(track),
            AddedAtUtc = DateTimeOffset.UtcNow
        };

        var insertIndex = _currentQueueIndex >= 0 && _currentQueueIndex < QueueItems.Count ? _currentQueueIndex + 1 : QueueItems.Count;
        QueueItems.Insert(insertIndex, item);
        if (_currentQueueIndex >= insertIndex && _currentQueueIndex >= 0)
        {
            _currentQueueIndex++;
        }

        _shuffleHistory.Clear();
        return insertIndex;
    }

    private int GetNextQueueIndex(bool manualAdvance)
    {
        if (QueueItems.Count == 0)
        {
            return -1;
        }

        if (_currentQueueIndex < 0 || _currentQueueIndex >= QueueItems.Count)
        {
            return 0;
        }

        if (ShuffleEnabled)
        {
            if (QueueItems.Count == 1)
            {
                return RepeatMode == RepeatMode.None && !manualAdvance ? -1 : 0;
            }

            var candidates = Enumerable.Range(0, QueueItems.Count).Where(i => i != _currentQueueIndex).ToList();
            return candidates.Count == 0 ? -1 : candidates[_random.Next(candidates.Count)];
        }

        var next = _currentQueueIndex + 1;
        if (next < QueueItems.Count)
        {
            return next;
        }

        return RepeatMode == RepeatMode.All ? 0 : -1;
    }

    private void AddRecent(SpotifyTrack track)
    {
        var existing = RecentTracks.FirstOrDefault(x => string.Equals(x.Track.Id, track.Id, StringComparison.Ordinal));
        if (existing is not null)
        {
            RecentTracks.Remove(existing);
        }

        RecentTracks.Insert(0, new RecentTrackEntry
        {
            Track = CloneTrack(track),
            PlayedAtUtc = DateTimeOffset.UtcNow
        });

        while (RecentTracks.Count > MaxRecentTracks)
        {
            RecentTracks.RemoveAt(RecentTracks.Count - 1);
        }
    }

    private void RefreshSelectedPlaylistTracks()
    {
        SelectedPlaylistTracks.Clear();
        if (SelectedPlaylist is not null)
        {
            foreach (var track in SelectedPlaylist.Tracks ?? [])
            {
                SelectedPlaylistTracks.Add(track);
            }
        }

        SelectedPlaylistTrack = SelectedPlaylistTracks.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedPlaylistSummary));
    }

    private void NudgePlaylistRefresh(PlaylistDefinition playlist)
    {
        var index = Playlists.IndexOf(playlist);
        if (index >= 0)
        {
            Playlists[index] = playlist;
        }
    }

    private void LoadSettingsCollections()
    {
        _suspendPersistence = true;
        try
        {
            foreach (var item in _settings.Queue ?? [])
            {
                QueueItems.Add(new QueuedTrack
                {
                    QueueItemId = string.IsNullOrWhiteSpace(item.QueueItemId) ? Guid.NewGuid().ToString("N") : item.QueueItemId,
                    Track = CloneTrack(item.Track),
                    AddedAtUtc = item.AddedAtUtc == default ? DateTimeOffset.UtcNow : item.AddedAtUtc
                });
            }

            foreach (var playlist in _settings.Playlists ?? [])
            {
                Playlists.Add(new PlaylistDefinition
                {
                    Id = string.IsNullOrWhiteSpace(playlist.Id) ? Guid.NewGuid().ToString("N") : playlist.Id,
                    Name = string.IsNullOrWhiteSpace(playlist.Name) ? GeneratePlaylistName() : playlist.Name,
                    Tracks = (playlist.Tracks ?? []).Select(CloneTrack).ToList(),
                    UpdatedAtUtc = playlist.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : playlist.UpdatedAtUtc
                });
            }

            foreach (var recent in (_settings.RecentTracks ?? []).Take(MaxRecentTracks))
            {
                RecentTracks.Add(new RecentTrackEntry
                {
                    Track = recent.Track is null ? CloneTrack(new SpotifyTrack(Guid.NewGuid().ToString("N"), "Unknown Track", "Unknown Artist", string.Empty, TimeSpan.Zero)) : CloneTrack(recent.Track),
                    PlayedAtUtc = recent.PlayedAtUtc == default ? DateTimeOffset.UtcNow : recent.PlayedAtUtc
                });
            }

            _currentQueueIndex = _settings.QueueCurrentIndex >= 0 && _settings.QueueCurrentIndex < QueueItems.Count
                ? _settings.QueueCurrentIndex
                : (QueueItems.Count > 0 ? 0 : -1);
            SelectedQueueItem = _currentQueueIndex >= 0 && _currentQueueIndex < QueueItems.Count ? QueueItems[_currentQueueIndex] : null;

            SelectedPlaylist = !string.IsNullOrWhiteSpace(_settings.ActivePlaylistId)
                ? Playlists.FirstOrDefault(x => string.Equals(x.Id, _settings.ActivePlaylistId, StringComparison.Ordinal))
                : Playlists.FirstOrDefault();

            if (SelectedPlaylist is not null)
            {
                DraftPlaylistName = SelectedPlaylist.Name;
            }
        }
        finally
        {
            _suspendPersistence = false;
        }
    }

    private void SyncSettingsCollections()
    {
        _settings.QueueCurrentIndex = _currentQueueIndex;
        _settings.ActivePlaylistId = SelectedPlaylist?.Id;
        _settings.Queue = QueueItems.Select(static q => new QueuedTrack { QueueItemId = q.QueueItemId, Track = CloneTrack(q.Track), AddedAtUtc = q.AddedAtUtc }).ToList();
        _settings.Playlists = Playlists.Select(static p => new PlaylistDefinition { Id = p.Id, Name = p.Name, UpdatedAtUtc = p.UpdatedAtUtc, Tracks = (p.Tracks ?? []).Select(CloneTrack).ToList() }).ToList();
        _settings.RecentTracks = RecentTracks.Select(static r => new RecentTrackEntry { Track = r.Track is null ? new SpotifyTrack(Guid.NewGuid().ToString("N"), "Unknown Track", "Unknown Artist", string.Empty, TimeSpan.Zero) : CloneTrack(r.Track), PlayedAtUtc = r.PlayedAtUtc }).ToList();
    }

    private void PersistSettingsQuietly()
    {
        if (_suspendPersistence || _disposed)
        {
            return;
        }

        try
        {
            SyncSettingsCollections();
            _settingsStore.Save(_settings);
        }
        catch
        {
        }
    }

    private void RefreshAllDerivedState()
    {
        OnPropertiesChanged(nameof(SearchSummary), nameof(ResolveSummary), nameof(QueueSummary), nameof(QueueDurationLabel), nameof(QueuePositionLabel), nameof(UpNextLabel), nameof(QueueModeSummary), nameof(SourcePickerSummary), nameof(PlaylistSummary), nameof(RecentsSummary), nameof(SelectedPlaylistSummary));
        RefreshCommandStates();
    }

    private void RefreshCommandStates()
    {
        _searchCommand?.RaiseCanExecuteChanged();
        _playSelectedCommand?.RaiseCanExecuteChanged();
        _queueSelectedCommand?.RaiseCanExecuteChanged();
        _queueTopResultsCommand?.RaiseCanExecuteChanged();
        _pauseResumeCommand?.RaiseCanExecuteChanged();
        _stopCommand?.RaiseCanExecuteChanged();
        _previousTrackCommand?.RaiseCanExecuteChanged();
        _nextTrackCommand?.RaiseCanExecuteChanged();
        _clearQueueCommand?.RaiseCanExecuteChanged();
        _shuffleQueueCommand?.RaiseCanExecuteChanged();
        _surpriseMixCommand?.RaiseCanExecuteChanged();
        _saveQueueAsPlaylistCommand?.RaiseCanExecuteChanged();
        _deletePlaylistCommand?.RaiseCanExecuteChanged();
        _loadPlaylistToQueueCommand?.RaiseCanExecuteChanged();
        _appendPlaylistToQueueCommand?.RaiseCanExecuteChanged();
        _addSelectedResultToPlaylistCommand?.RaiseCanExecuteChanged();
        _clearRecentsCommand?.RaiseCanExecuteChanged();
        _playSelectedSourceCommand?.RaiseCanExecuteChanged();
        _refreshSourceOptionsCommand?.RaiseCanExecuteChanged();
    }

    private static void ShuffleList<T>(IList<T> items)
    {
        var random = new Random();
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private string BuildUpNextLabel()
    {
        if (QueueItems.Count == 0)
        {
            return "Up next: queue a track to keep the music going";
        }

        if (ShuffleEnabled)
        {
            return "Up next: shuffle mode will pick a random track";
        }

        if (_currentQueueIndex >= 0 && _currentQueueIndex + 1 < QueueItems.Count)
        {
            var next = QueueItems[_currentQueueIndex + 1].Track;
            return $"Up next: {next.Title} • {next.Artists}";
        }

        return RepeatMode == RepeatMode.All && QueueItems.Count > 0
            ? $"Up next: loops back to {QueueItems[0].Track.Title}"
            : "Up next: end of queue";
    }

    private string BuildSourcePickerSummary()
    {
        if (_currentQueueIndex < 0 || _currentQueueIndex >= QueueItems.Count)
        {
            return "Source picker: play a track first to compare YouTube uploads.";
        }

        if (SourceOptions.Count == 0)
        {
            return IsResolving
                ? "Loading YouTube source options..."
                : "No alternate sources loaded yet.";
        }

        var selected = SelectedSourceOption;
        return selected is null
            ? $"{SourceOptions.Count} YouTube sources available."
            : $"{SourceOptions.Count} source options • selected: {selected.Channel}";
    }

    private void UpdateNowPlayingFromQueuePointer(bool clearIfNone = false)
    {
        if (_currentQueueIndex < 0 || _currentQueueIndex >= QueueItems.Count)
        {
            if (clearIfNone)
            {
                NowPlayingTitle = "No track loaded";
                NowPlayingMeta = "Search Apple/iTunes metadata and pick a result";
                NowPlayingSource = "Audio-only YouTube playback via native libVLC";
                _currentSourcePickerTrackId = null;
                SourceOptions.Clear();
                SelectedSourceOption = null;
            }
            return;
        }

        var track = QueueItems[_currentQueueIndex].Track;
        NowPlayingTitle = track.Title;
        NowPlayingMeta = track.Subtitle;
        if (!HasPlayback)
        {
            NowPlayingSource = "Queued and ready to play";
        }
    }

    private string GeneratePlaylistName()
    {
        var baseName = $"Playlist {DateTime.Now:MMM d}";
        if (Playlists.All(p => !string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        var i = 2;
        while (Playlists.Any(p => string.Equals(p.Name, $"{baseName} {i}", StringComparison.OrdinalIgnoreCase)))
        {
            i++;
        }

        return $"{baseName} {i}";
    }

    private static SpotifyTrack CloneTrack(SpotifyTrack track) => new(
        string.IsNullOrWhiteSpace(track.Id) ? Guid.NewGuid().ToString("N") : track.Id,
        track.Title ?? string.Empty,
        track.Artists ?? string.Empty,
        track.Album ?? string.Empty,
        track.Duration < TimeSpan.Zero ? TimeSpan.Zero : track.Duration);

    private static string FormatTime(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "0:00";
        }

        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }
}

