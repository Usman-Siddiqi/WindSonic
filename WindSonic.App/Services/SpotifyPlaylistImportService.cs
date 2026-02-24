using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindSonic.App.Services;

public sealed class SpotifyPlaylistImportService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly Regex PlaylistIdRegex = new(
        @"(?:spotify:playlist:|open\.spotify\.com/playlist/)?(?<id>[A-Za-z0-9]{22})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScriptRegex = new(
        @"<script[^>]*>(?<body>.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public async Task<SpotifyPlaylistImportResult> GetPlaylistTracksAsync(
        string playlistInput,
        string? spotifyClientId,
        string? spotifyClientSecret,
        CancellationToken cancellationToken)
    {
        var playlistId = ParsePlaylistId(playlistInput);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new InvalidOperationException("Paste a Spotify playlist URL, URI, or 22-character playlist ID.");
        }

        string? apiFailure = null;
        if (!string.IsNullOrWhiteSpace(spotifyClientId) && !string.IsNullOrWhiteSpace(spotifyClientSecret))
        {
            try
            {
                return await GetViaSpotifyWebApiAsync(
                        playlistId,
                        spotifyClientId.Trim(),
                        spotifyClientSecret.Trim(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                apiFailure = ex.Message;
            }
        }

        var fallback = await GetViaPublicPageAsync(playlistId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(apiFailure))
        {
            fallback.Warning = $"Spotify API fallback used ({apiFailure})";
        }

        return fallback;
    }

    private async Task<SpotifyPlaylistImportResult> GetViaSpotifyWebApiAsync(
        string playlistId,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var accessToken = await AcquireClientCredentialsTokenAsync(clientId, clientSecret, cancellationToken)
            .ConfigureAwait(false);

        var playlistName = await GetPlaylistNameAsync(playlistId, accessToken, cancellationToken).ConfigureAwait(false);
        var tracks = new List<SpotifyPlaylistImportTrack>();

        var offset = 0;
        int? totalCount = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"https://api.spotify.com/v1/playlists/{playlistId}/tracks?limit=100&offset={offset}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw BuildSpotifyError("Spotify playlist import failed", response.StatusCode, payload);
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (totalCount is null &&
                root.TryGetProperty("total", out var totalNode) &&
                totalNode.TryGetInt32(out var totalValue))
            {
                totalCount = Math.Max(0, totalValue);
            }

            var pageItems = 0;
            if (root.TryGetProperty("items", out var itemsNode) && itemsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsNode.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!item.TryGetProperty("track", out var trackNode) || trackNode.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var parsed = TryParseSpotifyApiTrack(trackNode);
                    if (parsed is null)
                    {
                        continue;
                    }

                    tracks.Add(parsed);
                    pageItems++;
                }
            }

            var next = root.TryGetProperty("next", out var nextNode) ? nextNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(next) || pageItems == 0)
            {
                break;
            }

            offset += 100;
            if (tracks.Count >= 5000)
            {
                break;
            }
        }

        return new SpotifyPlaylistImportResult
        {
            PlaylistId = playlistId,
            PlaylistName = string.IsNullOrWhiteSpace(playlistName) ? $"Spotify Playlist {playlistId}" : playlistName,
            Tracks = tracks,
            LoadedTrackCount = tracks.Count,
            TotalTrackCount = totalCount ?? tracks.Count,
            SourceLabel = "Spotify Web API",
            UsedApi = true
        };
    }

    private async Task<string> AcquireClientCredentialsTokenAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "client_credentials")
        ]);

        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw BuildSpotifyError("Spotify auth failed", response.StatusCode, payload);
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenNode))
        {
            throw new InvalidOperationException("Spotify auth succeeded but no access token was returned.");
        }

        var token = tokenNode.GetString();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Spotify auth returned an empty access token.");
        }

        return token;
    }

    private async Task<string> GetPlaylistNameAsync(
        string playlistId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.spotify.com/v1/playlists/{playlistId}?fields=name";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw BuildSpotifyError("Spotify playlist lookup failed", response.StatusCode, payload);
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("name", out var nameNode)
            ? (nameNode.GetString() ?? $"Spotify Playlist {playlistId}")
            : $"Spotify Playlist {playlistId}";
    }

    private async Task<SpotifyPlaylistImportResult> GetViaPublicPageAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://open.spotify.com/playlist/{playlistId}");
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Spotify playlist page failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        var decodedState = ExtractLargestBase64JsonBlob(html);
        if (string.IsNullOrWhiteSpace(decodedState))
        {
            throw new InvalidOperationException("Could not parse Spotify playlist page. Try a public playlist URL.");
        }

        using var document = JsonDocument.Parse(decodedState);
        var root = document.RootElement;

        if (!root.TryGetProperty("entities", out var entitiesNode) ||
            !entitiesNode.TryGetProperty("items", out var itemsNode) ||
            itemsNode.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Spotify page data did not include playlist entities.");
        }

        var entityKey = $"spotify:playlist:{playlistId}";
        if (!itemsNode.TryGetProperty(entityKey, out var playlistNode) || playlistNode.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Spotify page did not include that playlist.");
        }

        var playlistName = playlistNode.TryGetProperty("name", out var playlistNameNode)
            ? (playlistNameNode.GetString() ?? $"Spotify Playlist {playlistId}")
            : $"Spotify Playlist {playlistId}";

        var tracks = new List<SpotifyPlaylistImportTrack>();
        var totalCount = 0;
        var loadedCount = 0;
        if (playlistNode.TryGetProperty("content", out var contentNode) && contentNode.ValueKind == JsonValueKind.Object)
        {
            if (contentNode.TryGetProperty("totalCount", out var totalCountNode) && totalCountNode.TryGetInt32(out var total))
            {
                totalCount = Math.Max(total, 0);
            }

            if (contentNode.TryGetProperty("items", out var trackItemsNode) && trackItemsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in trackItemsNode.EnumerateArray())
                {
                    if (!item.TryGetProperty("itemV2", out var itemV2Node) || itemV2Node.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!itemV2Node.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var parsed = TryParseSpotifyPageTrack(dataNode);
                    if (parsed is null)
                    {
                        continue;
                    }

                    tracks.Add(parsed);
                }

                loadedCount = tracks.Count;
            }
        }

        return new SpotifyPlaylistImportResult
        {
            PlaylistId = playlistId,
            PlaylistName = playlistName,
            Tracks = tracks,
            LoadedTrackCount = loadedCount,
            TotalTrackCount = totalCount > 0 ? totalCount : loadedCount,
            SourceLabel = "Spotify public playlist page",
            UsedApi = false
        };
    }

    private static SpotifyPlaylistImportTrack? TryParseSpotifyApiTrack(JsonElement trackNode)
    {
        var typeName = trackNode.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
        if (!string.IsNullOrWhiteSpace(typeName) &&
            !string.Equals(typeName, "track", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = trackNode.TryGetProperty("name", out var titleNode) ? titleNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artists = ExtractArtistsFromApiTrack(trackNode);
        if (string.IsNullOrWhiteSpace(artists))
        {
            artists = "Unknown Artist";
        }

        var album = trackNode.TryGetProperty("album", out var albumNode) &&
                    albumNode.ValueKind == JsonValueKind.Object &&
                    albumNode.TryGetProperty("name", out var albumNameNode)
            ? (albumNameNode.GetString() ?? string.Empty)
            : string.Empty;

        var duration = trackNode.TryGetProperty("duration_ms", out var durationNode) && durationNode.TryGetInt64(out var durationMs)
            ? TimeSpan.FromMilliseconds(Math.Max(0, durationMs))
            : TimeSpan.Zero;

        var uri = trackNode.TryGetProperty("uri", out var uriNode) ? (uriNode.GetString() ?? string.Empty) : string.Empty;
        var id = trackNode.TryGetProperty("id", out var idNode) ? (idNode.GetString() ?? string.Empty) : string.Empty;

        return new SpotifyPlaylistImportTrack(id, title, artists, album, duration, uri);
    }

    private static SpotifyPlaylistImportTrack? TryParseSpotifyPageTrack(JsonElement dataNode)
    {
        var typeName = dataNode.TryGetProperty("__typename", out var typenameNode) ? typenameNode.GetString() : null;
        if (!string.IsNullOrWhiteSpace(typeName) &&
            !string.Equals(typeName, "Track", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = dataNode.TryGetProperty("name", out var titleNode) ? titleNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artists = ExtractArtistsFromPageTrack(dataNode);
        if (string.IsNullOrWhiteSpace(artists))
        {
            artists = "Unknown Artist";
        }

        var album = dataNode.TryGetProperty("albumOfTrack", out var albumNode) &&
                    albumNode.ValueKind == JsonValueKind.Object &&
                    albumNode.TryGetProperty("name", out var albumNameNode)
            ? (albumNameNode.GetString() ?? string.Empty)
            : string.Empty;

        var duration = TimeSpan.Zero;
        if (dataNode.TryGetProperty("duration", out var durationNode) &&
            durationNode.ValueKind == JsonValueKind.Object &&
            durationNode.TryGetProperty("totalMilliseconds", out var totalMsNode) &&
            totalMsNode.TryGetInt64(out var totalMs))
        {
            duration = TimeSpan.FromMilliseconds(Math.Max(0, totalMs));
        }

        var uri = dataNode.TryGetProperty("uri", out var uriNode) ? (uriNode.GetString() ?? string.Empty) : string.Empty;
        var id = ExtractSpotifyIdFromUri(uri);

        return new SpotifyPlaylistImportTrack(id, title, artists, album, duration, uri);
    }

    private static string ExtractArtistsFromApiTrack(JsonElement trackNode)
    {
        if (!trackNode.TryGetProperty("artists", out var artistsNode) || artistsNode.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var names = new List<string>();
        foreach (var artist in artistsNode.EnumerateArray())
        {
            if (artist.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = artist.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim());
            }
        }

        return string.Join(", ", names);
    }

    private static string ExtractArtistsFromPageTrack(JsonElement dataNode)
    {
        if (!dataNode.TryGetProperty("artists", out var artistsNode) ||
            artistsNode.ValueKind != JsonValueKind.Object ||
            !artistsNode.TryGetProperty("items", out var itemsNode) ||
            itemsNode.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var names = new List<string>();
        foreach (var artistNode in itemsNode.EnumerateArray())
        {
            if (artistNode.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!artistNode.TryGetProperty("profile", out var profileNode) || profileNode.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = profileNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim());
            }
        }

        return string.Join(", ", names);
    }

    private static string ExtractLargestBase64JsonBlob(string html)
    {
        string bestJson = string.Empty;
        foreach (Match match in ScriptRegex.Matches(html))
        {
            var body = match.Groups["body"].Value.Trim();
            if (body.Length < 100)
            {
                continue;
            }

            if (!LooksLikeBase64(body))
            {
                continue;
            }

            try
            {
                var bytes = Convert.FromBase64String(body);
                var decoded = Encoding.UTF8.GetString(bytes);
                if (decoded.Length <= bestJson.Length)
                {
                    continue;
                }

                if (!decoded.Contains("\"entities\"", StringComparison.Ordinal) ||
                    !decoded.Contains("\"spotify:playlist:", StringComparison.Ordinal))
                {
                    continue;
                }

                bestJson = decoded;
            }
            catch
            {
                // Skip non-base64 script blobs.
            }
        }

        return bestJson;
    }

    private static bool LooksLikeBase64(string value)
    {
        foreach (var c in value)
        {
            var ok = (c >= 'A' && c <= 'Z') ||
                     (c >= 'a' && c <= 'z') ||
                     (c >= '0' && c <= '9') ||
                     c is '+' or '/' or '=';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static string ParsePlaylistId(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 22 && trimmed.All(char.IsLetterOrDigit))
        {
            return trimmed;
        }

        var match = PlaylistIdRegex.Match(trimmed);
        return match.Success ? match.Groups["id"].Value : string.Empty;
    }

    private static string ExtractSpotifyIdFromUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return string.Empty;
        }

        var parts = uri.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[^1] : string.Empty;
    }

    private static Exception BuildSpotifyError(string prefix, HttpStatusCode statusCode, string payload)
    {
        var message = prefix;
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            message += " (rate limited by Spotify)";
        }

        var details = TryExtractSpotifyErrorMessage(payload);
        if (!string.IsNullOrWhiteSpace(details))
        {
            message += $": {details}";
        }
        else
        {
            message += $" ({(int)statusCode} {statusCode})";
        }

        return new InvalidOperationException(message);
    }

    private static string? TryExtractSpotifyErrorMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var errorNode))
            {
                if (errorNode.ValueKind == JsonValueKind.Object &&
                    errorNode.TryGetProperty("message", out var messageNode))
                {
                    return messageNode.GetString();
                }

                if (errorNode.ValueKind == JsonValueKind.String)
                {
                    return errorNode.GetString();
                }
            }

            if (root.TryGetProperty("message", out var topMessageNode))
            {
                return topMessageNode.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        int maxAttempts = 4)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var currentRequest = attempt == 1 ? request : CloneRequest(request);
            var response = await HttpClient.SendAsync(currentRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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
            return delta > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var computed = date - DateTimeOffset.UtcNow;
            if (computed > TimeSpan.Zero)
            {
                return computed > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : computed;
            }
        }

        var seconds = Math.Min(8, Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (source.Content is not null)
        {
            var contentBytes = source.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            clone.Content = new ByteArrayContent(contentBytes);
            foreach (var header in source.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindSonicImporter/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}

public sealed class SpotifyPlaylistImportResult
{
    public string PlaylistId { get; set; } = string.Empty;
    public string PlaylistName { get; set; } = string.Empty;
    public List<SpotifyPlaylistImportTrack> Tracks { get; set; } = [];
    public int LoadedTrackCount { get; set; }
    public int TotalTrackCount { get; set; }
    public string SourceLabel { get; set; } = string.Empty;
    public bool UsedApi { get; set; }
    public string? Warning { get; set; }
}

public sealed record SpotifyPlaylistImportTrack(
    string Id,
    string Title,
    string Artists,
    string Album,
    TimeSpan Duration,
    string SpotifyUri);
