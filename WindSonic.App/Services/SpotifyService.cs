using System.Net.Http;
using System.Net;
using System.Text.Json;
using WindSonic.App.Models;

namespace WindSonic.App.Services;

// Kept class name for minimal app wiring changes. This now uses Apple's public iTunes Search API
// for metadata search (no auth required), then WindSonic still plays the matched track from YouTube.
public sealed class SpotifyService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<IReadOnlyList<SpotifyTrack>> SearchTracksAsync(
        string query,
        string clientId,
        string clientSecret,
        int limit,
        CancellationToken cancellationToken)
    {
        _ = clientId;
        _ = clientSecret;

        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SpotifyTrack>();
        }

        var uri =
            $"https://itunes.apple.com/search?media=music&entity=song&country=us&limit={Math.Clamp(limit, 1, 200)}&term={Uri.EscapeDataString(query)}";

        using var response = await GetWithRetryAsync(uri, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"iTunes Search API failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("results", out var resultsNode) ||
            resultsNode.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SpotifyTrack>();
        }

        var results = new List<SpotifyTrack>(resultsNode.GetArrayLength());
        foreach (var item in resultsNode.EnumerateArray())
        {
            var kind = item.TryGetProperty("kind", out var kindNode) ? kindNode.GetString() : null;
            if (!string.IsNullOrWhiteSpace(kind) &&
                !string.Equals(kind, "song", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var trackName = item.TryGetProperty("trackName", out var trackNameNode) ? trackNameNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(trackName))
            {
                continue;
            }

            var trackId = item.TryGetProperty("trackId", out var trackIdNode) && trackIdNode.TryGetInt64(out var trackIdValue)
                ? trackIdValue.ToString()
                : Guid.NewGuid().ToString("N");

            var artistName = item.TryGetProperty("artistName", out var artistNameNode)
                ? (artistNameNode.GetString() ?? "Unknown Artist")
                : "Unknown Artist";

            var albumName = item.TryGetProperty("collectionName", out var albumNode)
                ? (albumNode.GetString() ?? string.Empty)
                : string.Empty;

            var durationMs = item.TryGetProperty("trackTimeMillis", out var durationNode) && durationNode.TryGetInt64(out var ms)
                ? Math.Max(ms, 0)
                : 0;

            results.Add(new SpotifyTrack(
                trackId,
                trackName,
                artistName,
                albumName,
                TimeSpan.FromMilliseconds(durationMs)));
        }

        return results;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression =
                System.Net.DecompressionMethods.GZip |
                System.Net.DecompressionMethods.Deflate |
                System.Net.DecompressionMethods.Brotli
        });

        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindSonicNative/1.0");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private static async Task<HttpResponseMessage> GetWithRetryAsync(string uri, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!ShouldRetry(response.StatusCode) || attempt == maxAttempts)
            {
                return response;
            }

            var delay = GetRetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Unexpected retry loop exit.");
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests ||
        statusCode == HttpStatusCode.BadGateway ||
        statusCode == HttpStatusCode.ServiceUnavailable ||
        statusCode == HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > TimeSpan.FromSeconds(12) ? TimeSpan.FromSeconds(12) : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var computed = date - DateTimeOffset.UtcNow;
            if (computed > TimeSpan.Zero)
            {
                return computed > TimeSpan.FromSeconds(12) ? TimeSpan.FromSeconds(12) : computed;
            }
        }

        var seconds = Math.Min(8, Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromSeconds(seconds);
    }
}

