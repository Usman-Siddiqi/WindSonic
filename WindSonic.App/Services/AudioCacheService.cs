using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;
using WindSonic.App.Models;

namespace WindSonic.App.Services;

public sealed class AudioCacheService
{
    private const long MaxCacheBytesLimit = 1024L * 1024L * 1024L; // 1 GiB

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly object _sync = new();
    private readonly string _cacheDirectory;
    private readonly string _indexPath;
    private readonly Dictionary<string, Task> _inflightDownloads = new(StringComparer.Ordinal);

    private CacheIndex _index = new();
    private bool _initialized;

    public AudioCacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = Path.Combine(appData, "WindSonic");
        _cacheDirectory = Path.Combine(root, "cache");
        _indexPath = Path.Combine(root, "cache-index.json");
    }

    public long MaxCacheBytes => MaxCacheBytesLimit;

    public bool TryGetCachedPath(SpotifyTrack track, YouTubeAudioSource source, out string? path)
    {
        path = null;

        try
        {
            EnsureInitialized();

            var key = BuildCacheKey(source);
            lock (_sync)
            {
                var entry = _index.Entries.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
                if (entry is null)
                {
                    return false;
                }

                var fullPath = Path.Combine(_cacheDirectory, entry.FileName);
                if (!File.Exists(fullPath))
                {
                    _index.Entries.Remove(entry);
                    SaveIndexUnsafe();
                    return false;
                }

                entry.LastPlayedUtc = DateTimeOffset.UtcNow;
                entry.TrackTitle = track.Title;
                entry.Artists = track.Artists;
                SaveIndexUnsafe();

                path = fullPath;
                return true;
            }
        }
        catch
        {
            // Cache failures must never break playback.
            return false;
        }
    }

    public void CacheInBackground(SpotifyTrack track, YouTubeAudioSource source)
    {
        try
        {
            EnsureInitialized();
            var key = BuildCacheKey(source);

            lock (_sync)
            {
                if (_inflightDownloads.ContainsKey(key))
                {
                    return;
                }

                var existing = _index.Entries.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
                if (existing is not null)
                {
                    var existingPath = Path.Combine(_cacheDirectory, existing.FileName);
                    if (File.Exists(existingPath))
                    {
                        return;
                    }

                    _index.Entries.Remove(existing);
                    SaveIndexUnsafe();
                }

                var task = DownloadAndStoreAsync(track, source, key);
                _inflightDownloads[key] = task;

                _ = task.ContinueWith(
                    _ =>
                    {
                        lock (_sync)
                        {
                            _inflightDownloads.Remove(key);
                        }
                    },
                    TaskScheduler.Default);
            }
        }
        catch
        {
            // No-op: fallback streaming still works.
        }
    }

    private async Task DownloadAndStoreAsync(SpotifyTrack track, YouTubeAudioSource source, string key)
    {
        string? tempPath = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.StreamUrl);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var extension = DetermineFileExtension(
                response.Content.Headers.ContentType,
                response.RequestMessage?.RequestUri);

            Directory.CreateDirectory(_cacheDirectory);

            var finalFileName = $"{key}{extension}";
            var finalPath = Path.Combine(_cacheDirectory, finalFileName);
            tempPath = Path.Combine(_cacheDirectory, $"{key}.{Guid.NewGuid():N}.tmp");

            long bytesWritten = 0;
            await using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true))
            {
                await input.CopyToAsync(output).ConfigureAwait(false);
                bytesWritten = output.Length;
            }

            if (bytesWritten <= 0)
            {
                return;
            }

            lock (_sync)
            {
                EnsureInitializedUnsafe();

                var existing = _index.Entries.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
                if (existing is not null)
                {
                    var existingPath = Path.Combine(_cacheDirectory, existing.FileName);
                    if (File.Exists(existingPath))
                    {
                        return;
                    }

                    _index.Entries.Remove(existing);
                }

                if (File.Exists(finalPath))
                {
                    try
                    {
                        File.Delete(finalPath);
                    }
                    catch
                    {
                        finalFileName = $"{key}-{Guid.NewGuid():N}{extension}";
                        finalPath = Path.Combine(_cacheDirectory, finalFileName);
                    }
                }

                File.Move(tempPath, finalPath, overwrite: false);
                tempPath = null;

                var now = DateTimeOffset.UtcNow;
                _index.Entries.Add(new CacheEntry
                {
                    Key = key,
                    VideoId = source.VideoId,
                    FileName = finalFileName,
                    FileSizeBytes = bytesWritten,
                    TrackTitle = track.Title,
                    Artists = track.Artists,
                    SourceTitle = source.Title,
                    Channel = source.Channel,
                    CreatedUtc = now,
                    LastPlayedUtc = now
                });

                EnforceCacheLimitUnsafe();
                SaveIndexUnsafe();
            }
        }
        catch
        {
            // Ignore cache download failures; playback already falls back to streaming.
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private void EnsureInitialized()
    {
        lock (_sync)
        {
            EnsureInitializedUnsafe();
        }
    }

    private void EnsureInitializedUnsafe()
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(_cacheDirectory);
        _index = LoadIndexUnsafe();
        PruneMissingFilesUnsafe();
        EnforceCacheLimitUnsafe();
        SaveIndexUnsafe();
        _initialized = true;
    }

    private CacheIndex LoadIndexUnsafe()
    {
        try
        {
            if (!File.Exists(_indexPath))
            {
                return new CacheIndex();
            }

            var json = File.ReadAllText(_indexPath);
            return JsonSerializer.Deserialize<CacheIndex>(json) ?? new CacheIndex();
        }
        catch
        {
            return new CacheIndex();
        }
    }

    private void SaveIndexUnsafe()
    {
        var directory = Path.GetDirectoryName(_indexPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_index, JsonOptions);
        File.WriteAllText(_indexPath, json);
    }

    private void PruneMissingFilesUnsafe()
    {
        _index.Entries.RemoveAll(entry =>
            string.IsNullOrWhiteSpace(entry.FileName) ||
            !File.Exists(Path.Combine(_cacheDirectory, entry.FileName)));
    }

    private void EnforceCacheLimitUnsafe()
    {
        foreach (var entry in _index.Entries)
        {
            if (entry.FileSizeBytes <= 0 && !string.IsNullOrWhiteSpace(entry.FileName))
            {
                var path = Path.Combine(_cacheDirectory, entry.FileName);
                if (File.Exists(path))
                {
                    entry.FileSizeBytes = new FileInfo(path).Length;
                }
            }
        }

        long total = _index.Entries.Sum(x => Math.Max(0, x.FileSizeBytes));
        if (total <= MaxCacheBytesLimit)
        {
            return;
        }

        foreach (var victim in _index.Entries
                     .OrderBy(x => x.LastPlayedUtc == default ? DateTimeOffset.MinValue : x.LastPlayedUtc)
                     .ThenBy(x => x.CreatedUtc == default ? DateTimeOffset.MinValue : x.CreatedUtc)
                     .ToList())
        {
            var path = Path.Combine(_cacheDirectory, victim.FileName ?? string.Empty);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // If a file is locked, skip this victim and try the next one.
                continue;
            }

            total -= Math.Max(0, victim.FileSizeBytes);
            _index.Entries.Remove(victim);
            if (total <= MaxCacheBytesLimit)
            {
                break;
            }
        }
    }

    private static string BuildCacheKey(YouTubeAudioSource source) =>
        string.IsNullOrWhiteSpace(source.VideoId) ? Guid.NewGuid().ToString("N") : source.VideoId;

    private static string DetermineFileExtension(MediaTypeHeaderValue? contentType, Uri? requestUri)
    {
        var mediaType = contentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
        if (mediaType.Contains("webm"))
        {
            return ".webm";
        }

        if (mediaType.Contains("mp4") || mediaType.Contains("m4a"))
        {
            return ".m4a";
        }

        if (mediaType.Contains("mpeg") || mediaType.Contains("mp3"))
        {
            return ".mp3";
        }

        var path = requestUri?.AbsolutePath;
        var ext = !string.IsNullOrWhiteSpace(path) ? Path.GetExtension(path) : null;
        if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 6)
        {
            return ext;
        }

        return ".audio";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate |
                                     System.Net.DecompressionMethods.Brotli
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindSonicCache/1.0");
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    private sealed class CacheIndex
    {
        public List<CacheEntry> Entries { get; set; } = [];
    }

    private sealed class CacheEntry
    {
        public string Key { get; set; } = string.Empty;
        public string VideoId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string TrackTitle { get; set; } = string.Empty;
        public string Artists { get; set; } = string.Empty;
        public string SourceTitle { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastPlayedUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
