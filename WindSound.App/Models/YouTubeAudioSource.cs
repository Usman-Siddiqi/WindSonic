namespace WindSound.App.Models;

public sealed record YouTubeAudioSource(
    string VideoId,
    string Title,
    string Channel,
    TimeSpan? Duration,
    string StreamUrl,
    string SearchQueryUsed)
{
    public int MatchScore { get; init; }

    public string DurationLabel =>
        Duration is { } d && d > TimeSpan.Zero
            ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
            : "--:--";

    public string PickerLabel => $"{Title} • {Channel} • {DurationLabel}";

    public override string ToString() => PickerLabel;
}
