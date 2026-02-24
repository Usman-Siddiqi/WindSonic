using WindSound.App.Models;
using YoutubeExplode;
using YoutubeExplode.Search;
using YoutubeExplode.Videos.Streams;

namespace WindSound.App.Services;

public sealed class YouTubeAudioResolverService
{
    private readonly YoutubeClient _youtubeClient = new();
    private sealed record CandidateVideo(VideoSearchResult Video, int Score);

    public async Task<YouTubeAudioSource> ResolveForTrackAsync(SpotifyTrack track, CancellationToken cancellationToken)
    {
        var candidates = await ResolveCandidatesForTrackAsync(track, 1, cancellationToken).ConfigureAwait(false);
        return candidates.First();
    }

    public async Task<IReadOnlyList<YouTubeAudioSource>> ResolveCandidatesForTrackAsync(
        SpotifyTrack track,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxCandidates, 1, 8);
        var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<YouTubeAudioSource>(limit);

        foreach (var query in BuildQueries(track))
        {
            var queryCandidates = await TryResolveCandidatesBySearchQueryAsync(query, track.Duration, limit, cancellationToken)
                .ConfigureAwait(false);

            foreach (var candidate in queryCandidates)
            {
                if (!seenVideoIds.Add(candidate.VideoId))
                {
                    continue;
                }

                results.Add(candidate);
                if (results.Count >= limit)
                {
                    return results
                        .OrderByDescending(static x => x.MatchScore)
                        .ThenBy(static x => x.Duration is null ? 1 : 0)
                        .ToList();
                }
            }
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException("No playable YouTube audio stream was found for that track.");
        }

        return results
            .OrderByDescending(static x => x.MatchScore)
            .ThenBy(static x => x.Duration is null ? 1 : 0)
            .ToList();
    }

    private async Task<IReadOnlyList<YouTubeAudioSource>> TryResolveCandidatesBySearchQueryAsync(
        string query,
        TimeSpan expectedDuration,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var candidates = new List<CandidateVideo>();
        var inspected = 0;
        var resolved = new List<YouTubeAudioSource>(Math.Clamp(maxCandidates, 1, 8));

        await foreach (var video in _youtubeClient.Search.GetVideosAsync(query, cancellationToken))
        {
            inspected++;

            if (video.Duration is null)
            {
                if (inspected >= 12)
                {
                    break;
                }

                continue;
            }

            var score = ScoreCandidate(query, expectedDuration, video.Title, video.Author.ChannelTitle, video.Duration.Value);
            candidates.Add(new CandidateVideo(video, score));

            if (inspected >= 8)
            {
                break;
            }
        }

        foreach (var candidate in candidates.OrderByDescending(static x => x.Score))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var manifest = await _youtubeClient.Videos.Streams
                    .GetManifestAsync(candidate.Video.Id, cancellationToken)
                    .ConfigureAwait(false);

                IAudioStreamInfo? audioStream = manifest
                    .GetAudioOnlyStreams()
                    .Where(static s => s.Bitrate.BitsPerSecond > 0)
                    .OrderByDescending(static s => s.Bitrate.BitsPerSecond)
                    .FirstOrDefault();

                if (audioStream is null)
                {
                    continue;
                }

                resolved.Add(new YouTubeAudioSource(
                    candidate.Video.Id.Value,
                    candidate.Video.Title,
                    candidate.Video.Author.ChannelTitle,
                    candidate.Video.Duration,
                    audioStream.Url,
                    query)
                {
                    MatchScore = candidate.Score
                });

                if (resolved.Count >= maxCandidates)
                {
                    break;
                }
            }
            catch
            {
                // Try the next candidate instead of failing the entire playback request.
            }
        }

        return resolved;
    }

    private static IEnumerable<string> BuildQueries(SpotifyTrack track)
    {
        yield return $"{track.Artists} {track.Title} official audio";
        yield return $"{track.Artists} {track.Title} topic";
        yield return $"{track.Artists} {track.Title}";
    }

    private static int ScoreCandidate(
        string query,
        TimeSpan expectedDuration,
        string title,
        string channel,
        TimeSpan duration)
    {
        var score = 0;
        var text = $"{title} {channel}".ToLowerInvariant();
        var queryText = query.ToLowerInvariant();

        if (text.Contains("official"))
        {
            score += 4;
        }

        if (text.Contains("audio"))
        {
            score += 4;
        }

        if (text.Contains("topic"))
        {
            score += 3;
        }

        if (text.Contains("lyrics"))
        {
            score -= 1;
        }

        if (text.Contains("live"))
        {
            score -= 4;
        }

        if (text.Contains("concert"))
        {
            score -= 4;
        }

        if (queryText.Contains("official audio") && text.Contains("official audio"))
        {
            score += 4;
        }

        var deltaSeconds = Math.Abs((duration - expectedDuration).TotalSeconds);
        if (deltaSeconds <= 5)
        {
            score += 8;
        }
        else if (deltaSeconds <= 15)
        {
            score += 5;
        }
        else if (deltaSeconds <= 45)
        {
            score += 2;
        }
        else if (deltaSeconds > 150)
        {
            score -= 5;
        }

        return score;
    }
}
