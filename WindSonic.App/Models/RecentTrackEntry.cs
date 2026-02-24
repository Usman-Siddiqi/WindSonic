namespace WindSonic.App.Models;

public sealed class RecentTrackEntry
{
    public SpotifyTrack Track { get; set; } = new(
        Guid.NewGuid().ToString("N"),
        "Unknown Track",
        "Unknown Artist",
        string.Empty,
        TimeSpan.Zero);

    public DateTimeOffset PlayedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string PlayedAtLabel => PlayedAtUtc.ToLocalTime().ToString("MMM d h:mm tt");
}

