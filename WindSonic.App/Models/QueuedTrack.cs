namespace WindSonic.App.Models;

public sealed class QueuedTrack
{
    public string QueueItemId { get; set; } = Guid.NewGuid().ToString("N");

    public SpotifyTrack Track { get; set; } = new(
        Guid.NewGuid().ToString("N"),
        "Unknown Track",
        "Unknown Artist",
        string.Empty,
        TimeSpan.Zero);

    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Title => Track?.Title ?? "Unknown Track";

    public string Subtitle => Track?.Subtitle ?? "Unknown Artist";

    public string DurationLabel => Track?.DurationLabel ?? "0:00";
}

