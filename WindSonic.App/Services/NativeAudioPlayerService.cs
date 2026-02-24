using System.Windows.Threading;
using LibVLCSharp.Shared;
using WindSonic.App.Models;

namespace WindSonic.App.Services;

public sealed class NativeAudioPlayerService : IDisposable
{
    private static bool _vlcInitialized;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _snapshotTimer;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _activeMedia;
    private bool _disposed;
    private bool _hasMedia;
    private bool _isPaused;
    private int _requestedVolume = 72;
    private TimeSpan _fallbackDuration = TimeSpan.Zero;

    public NativeAudioPlayerService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        _snapshotTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _snapshotTimer.Tick += (_, _) => PublishSnapshot();

        Volume = 72;
    }

    public event EventHandler<PlayerSnapshot>? SnapshotChanged;

    public event EventHandler<string>? PlaybackError;

    public event EventHandler? PlaybackEnded;

    public int Volume
    {
        get => _mediaPlayer?.Volume ?? _requestedVolume;
        set
        {
            _requestedVolume = Math.Clamp(value, 0, 100);
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Volume = _requestedVolume;
            }
        }
    }

    public PlayerSnapshot CurrentSnapshot =>
        BuildSnapshot();

    public void PlayStream(string streamUrl, TimeSpan? knownDuration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsurePlayerCreated();

        if (_libVlc is null || _mediaPlayer is null)
        {
            throw new InvalidOperationException("Audio engine initialization failed.");
        }

        _activeMedia?.Dispose();
        _activeMedia = new Media(_libVlc, new Uri(streamUrl));
        _activeMedia.AddOption(":no-video");
        _activeMedia.AddOption(":network-caching=300");
        _activeMedia.AddOption(":demux=any");

        _hasMedia = true;
        _isPaused = false;
        _fallbackDuration = knownDuration.GetValueOrDefault();

        if (!_mediaPlayer.Play(_activeMedia))
        {
            RaisePlaybackError("libVLC could not start playback for the selected YouTube stream.");
        }

        _snapshotTimer.Start();
        PublishSnapshot();
    }

    public bool Seek(TimeSpan targetPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_hasMedia || _mediaPlayer is null)
        {
            return false;
        }

        var effectiveDuration = GetEffectiveDuration();
        if (effectiveDuration > TimeSpan.Zero)
        {
            targetPosition = Clamp(targetPosition, TimeSpan.Zero, effectiveDuration);
        }
        else if (targetPosition < TimeSpan.Zero)
        {
            targetPosition = TimeSpan.Zero;
        }

        try
        {
            var targetMs = (long)Math.Max(0, targetPosition.TotalMilliseconds);

            // Many YouTube streams expose duration but not an updating absolute time. Set both time and
            // normalized position to maximize compatibility across container types.
            _mediaPlayer.Time = targetMs;

            if (effectiveDuration > TimeSpan.FromMilliseconds(1))
            {
                var normalized = (float)Math.Clamp(
                    targetPosition.TotalMilliseconds / effectiveDuration.TotalMilliseconds,
                    0d,
                    0.9999d);
                _mediaPlayer.Position = normalized;
            }

            PublishSnapshot();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void TogglePauseResume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_hasMedia || _mediaPlayer is null)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.SetPause(true);
            _isPaused = true;
        }
        else if (_isPaused)
        {
            _mediaPlayer.SetPause(false);
            _isPaused = false;
        }
        else
        {
            _mediaPlayer.Play();
            _isPaused = false;
        }

        PublishSnapshot();
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Stop();
        _isPaused = false;
        _fallbackDuration = TimeSpan.Zero;
        _snapshotTimer.Stop();
        PublishSnapshot();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _snapshotTimer.Stop();

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Playing -= OnMediaStateChanged;
            _mediaPlayer.Paused -= OnMediaStateChanged;
            _mediaPlayer.Stopped -= OnMediaStateChanged;
            _mediaPlayer.EndReached -= OnMediaEndReached;
            _mediaPlayer.EncounteredError -= OnMediaError;
        }

        _activeMedia?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }

    private static void EnsureLibVlcInitialized()
    {
        if (_vlcInitialized)
        {
            return;
        }

        Core.Initialize();
        _vlcInitialized = true;
    }

    private void EnsurePlayerCreated()
    {
        if (_mediaPlayer is not null && _libVlc is not null)
        {
            return;
        }

        EnsureLibVlcInitialized();

        _libVlc = new LibVLC(
            "--no-video",
            "--quiet",
            "--network-caching=300",
            "--file-caching=200",
            "--no-snapshot-preview",
            "--no-metadata-network-access");

        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Playing += OnMediaStateChanged;
        _mediaPlayer.Paused += OnMediaStateChanged;
        _mediaPlayer.Stopped += OnMediaStateChanged;
        _mediaPlayer.EndReached += OnMediaEndReached;
        _mediaPlayer.EncounteredError += OnMediaError;
        _mediaPlayer.Volume = _requestedVolume;
    }

    private void OnMediaStateChanged(object? sender, EventArgs e)
    {
        PublishSnapshotOnUiThread();
    }

    private void OnMediaEndReached(object? sender, EventArgs e)
    {
        _isPaused = false;
        _fallbackDuration = TimeSpan.Zero;
        if (_dispatcher.CheckAccess())
        {
            PublishSnapshot();
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            PublishSnapshot();
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnMediaError(object? sender, EventArgs e)
    {
        RaisePlaybackError("Playback error from libVLC (YouTube stream may have expired or been throttled).");
        PublishSnapshotOnUiThread();
    }

    private void PublishSnapshotOnUiThread()
    {
        if (_dispatcher.CheckAccess())
        {
            PublishSnapshot();
            return;
        }

        _dispatcher.BeginInvoke(PublishSnapshot);
    }

    private void RaisePlaybackError(string message)
    {
        if (_dispatcher.CheckAccess())
        {
            PlaybackError?.Invoke(this, message);
        }
        else
        {
            _dispatcher.BeginInvoke(() => PlaybackError?.Invoke(this, message));
        }
    }

    private void PublishSnapshot()
    {
        var snapshot = BuildSnapshot();

        if ((_mediaPlayer is null || !_mediaPlayer.IsPlaying) &&
            !_isPaused &&
            snapshot.Position == TimeSpan.Zero &&
            snapshot.Duration == TimeSpan.Zero)
        {
            _snapshotTimer.Stop();
        }

        SnapshotChanged?.Invoke(this, snapshot);
    }

    private static TimeSpan MillisecondsToTimeSpan(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private PlayerSnapshot BuildSnapshot()
    {
        var mediaPlayer = _mediaPlayer;
        var duration = GetEffectiveDuration();
        var position = GetEffectivePosition(duration);

        return new PlayerSnapshot(
            _hasMedia,
            mediaPlayer?.IsPlaying ?? false,
            _hasMedia && _isPaused && !(mediaPlayer?.IsPlaying ?? false),
            position,
            duration);
    }

    private TimeSpan GetEffectiveDuration()
    {
        var libVlcDuration = MillisecondsToTimeSpan(_mediaPlayer?.Length ?? 0);
        if (libVlcDuration > TimeSpan.Zero)
        {
            return libVlcDuration;
        }

        return _fallbackDuration > TimeSpan.Zero ? _fallbackDuration : TimeSpan.Zero;
    }

    private TimeSpan GetEffectivePosition(TimeSpan effectiveDuration)
    {
        var absoluteTime = MillisecondsToTimeSpan(_mediaPlayer?.Time ?? 0);
        if (absoluteTime > TimeSpan.Zero)
        {
            if (effectiveDuration > TimeSpan.Zero && absoluteTime > effectiveDuration)
            {
                return effectiveDuration;
            }

            return absoluteTime;
        }

        var normalizedPosition = _mediaPlayer?.Position ?? 0f;
        if (normalizedPosition > 0f && effectiveDuration > TimeSpan.Zero)
        {
            var computed = TimeSpan.FromMilliseconds(effectiveDuration.TotalMilliseconds * normalizedPosition);
            return computed > effectiveDuration ? effectiveDuration : computed;
        }

        return TimeSpan.Zero;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}

