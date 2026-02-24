namespace WindSound.App.Models;

public sealed class PlaylistDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Playlist";

    public List<SpotifyTrack> Tracks { get; set; } = [];

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Summary
    {
        get
        {
            var count = Tracks?.Count ?? 0;
            return $"{count} track{(count == 1 ? string.Empty : "s")}";
        }
    }

    public override string ToString() => Name;
}
