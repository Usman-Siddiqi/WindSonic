namespace WindSound.App.Models;

public sealed class AppSettings
{
    public string SpotifyClientId { get; set; } = string.Empty;

    public string SpotifyClientSecret { get; set; } = string.Empty;

    public int Volume { get; set; } = 72;

    public bool ShuffleEnabled { get; set; }

    public RepeatMode RepeatMode { get; set; } = RepeatMode.All;

    public string? ActivePlaylistId { get; set; }

    public int QueueCurrentIndex { get; set; } = -1;

    public List<QueuedTrack> Queue { get; set; } = [];

    public List<PlaylistDefinition> Playlists { get; set; } = [];

    public List<RecentTrackEntry> RecentTracks { get; set; } = [];
}
