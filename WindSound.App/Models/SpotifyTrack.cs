namespace WindSound.App.Models;

public sealed record SpotifyTrack(
    string Id,
    string Title,
    string Artists,
    string Album,
    TimeSpan Duration)
{
    public string DurationLabel =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");

    public string Subtitle => string.IsNullOrWhiteSpace(Album) ? Artists : $"{Artists}  •  {Album}";

    public string SearchHint => $"{Artists} - {Title}";
}
