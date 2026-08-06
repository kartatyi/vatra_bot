using System.Text.Json;
using System.Text.Json.Serialization;
using LeBot.Application.Caching;
using LeBot.Application.Ports;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.MediaCache;

/// <summary>
/// The media cache on plain files: one directory per URL, holding the media plus an
/// <c>entry.json</c> describing it. A hit is a JSON read and a re-upload — the platform is never
/// contacted, which is the whole point: the second post of a link costs no network, no yt-dlp
/// process, and no chance of the platform having started rate-limiting us.
/// </summary>
/// <remarks>
/// Every operation is best-effort. A cache that can't read, write, or delete degrades to "always
/// miss" and logs; it never takes a repost down with it.
/// </remarks>
public sealed class FileSystemMediaCache : IMediaCache
{
    private const string EntryFileName = "entry.json";
    private const int MaxExtensionLength = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly MediaCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FileSystemMediaCache> _logger;

    public FileSystemMediaCache(
        IOptions<MediaCacheOptions> options,
        TimeProvider timeProvider,
        ILogger<FileSystemMediaCache> logger)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CachedRepost?> TryGetAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var key = MediaCacheKey.For(url);
        var entryDirectory = Path.Combine(_options.ResolvedDirectory, key.DirectoryName);

        try
        {
            var entryPath = Path.Combine(entryDirectory, EntryFileName);
            if (!File.Exists(entryPath))
            {
                return null;
            }

            // Read the whole file before parsing rather than streaming it: entry.json is a few
            // hundred bytes, and leaving no handle open means the eviction paths below can delete
            // the directory right away on Windows.
            var json = await File.ReadAllTextAsync(entryPath, cancellationToken);
            var document = JsonSerializer.Deserialize<CachedPayloadDocument>(json, JsonOptions);

            if (document is null
                || document.SchemaVersion != CachedPayloadDocument.CurrentSchemaVersion
                || !string.Equals(document.NormalizedUrl, key.NormalizedUrl, StringComparison.Ordinal))
            {
                // Written by an older build, or (astronomically unlikely) a hash collision. Either
                // way we don't know what these bytes are — drop them instead of reposting them.
                DeleteEntry(entryDirectory);
                return null;
            }

            if (IsExpired(document))
            {
                DeleteEntry(entryDirectory);
                _logger.LogDebug("Cache entry for {Url} aged out", url);
                return null;
            }

            var items = new List<MediaItem>(document.Items.Count);
            foreach (var cached in document.Items)
            {
                var path = Path.Combine(entryDirectory, cached.FileName);
                if (!File.Exists(path))
                {
                    // Someone emptied the folder under us; a partial album is worse than a re-extract.
                    DeleteEntry(entryDirectory);
                    _logger.LogDebug("Cache entry for {Url} is missing its media; discarded", url);
                    return null;
                }

                items.Add(new MediaItem(path, cached.Kind, cached.MimeType, cached.SizeBytes, cached.DurationSeconds));
            }

            var payload = new MediaPayload(
                document.SourceUrl,
                document.Title,
                document.Author,
                items,
                document.Description,
                RetainFiles: true);

            return new CachedRepost(payload, document.Extractor);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Cache entry for {Url} is corrupt; discarded", url);
            DeleteEntry(entryDirectory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the cache entry for {Url}", url);
            return null;
        }
    }

    public async Task SaveAsync(MediaPayload payload, string extractor, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || payload.RetainFiles)
        {
            // RetainFiles means these files came out of the cache in the first place — re-saving
            // them would be a copy onto themselves and would reset a lifetime that must not slide.
            return;
        }

        var key = MediaCacheKey.For(payload.SourceUrl);
        var entryDirectory = Path.Combine(_options.ResolvedDirectory, key.DirectoryName);

        try
        {
            DeleteEntry(entryDirectory);
            Directory.CreateDirectory(entryDirectory);

            var items = new List<CachedMediaItem>(payload.Items.Count);
            for (var index = 0; index < payload.Items.Count; index++)
            {
                var item = payload.Items[index];
                var fileName = $"{index:D2}{SafeExtension(item.FilePath)}";
                var destination = Path.Combine(entryDirectory, fileName);

                // Copy rather than move: the caller still has to send these files and then delete
                // them. One local copy is cheap next to a second extraction.
                File.Copy(item.FilePath, destination, overwrite: true);

                items.Add(new CachedMediaItem(
                    fileName,
                    item.Kind,
                    item.MimeType,
                    item.SizeBytes ?? new FileInfo(destination).Length,
                    item.DurationSeconds));
            }

            var document = new CachedPayloadDocument(
                CachedPayloadDocument.CurrentSchemaVersion,
                key.NormalizedUrl,
                payload.SourceUrl,
                extractor,
                payload.Title,
                payload.Author,
                payload.Description,
                _timeProvider.GetUtcNow(),
                items);

            await WriteEntryAsync(entryDirectory, document, cancellationToken);
            Prune();

            _logger.LogDebug(
                "Cached {Count} item(s) for {Url} from {Extractor}",
                items.Count, payload.SourceUrl, extractor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A half-written entry must never be served: drop what we managed to write and move on.
            _logger.LogWarning(ex, "Could not cache the repost for {Url}", payload.SourceUrl);
            DeleteEntry(entryDirectory);
        }
    }

    /// <summary>
    /// Deletes entries past their lifetime, then — if the cache is still over its size ceiling —
    /// the oldest of what's left until it fits. Returns how many entries were removed.
    /// </summary>
    /// <remarks>
    /// internal rather than private so the sweep can be exercised directly in a unit test, without
    /// the background service's real-time interval timer.
    /// </remarks>
    internal int Prune()
    {
        var root = _options.ResolvedDirectory;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var deleted = 0;
        var live = new List<(string Directory, DateTimeOffset CachedAt, long Size)>();

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var document = TryReadDocument(directory);
            if (document is null || IsExpired(document))
            {
                // Also catches directories left half-written by a crash: no readable entry.json,
                // no way to serve them, so they're just orphaned bytes.
                if (DeleteEntry(directory))
                {
                    deleted++;
                }

                continue;
            }

            live.Add((directory, document.CachedAtUtc, DirectorySize(directory)));
        }

        var total = live.Sum(entry => entry.Size);
        foreach (var entry in live.OrderBy(entry => entry.CachedAt))
        {
            if (total <= _options.MaxTotalSizeBytes)
            {
                break;
            }

            if (DeleteEntry(entry.Directory))
            {
                total -= entry.Size;
                deleted++;
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Media cache: evicted {Count} entr(ies)", deleted);
        }

        return deleted;
    }

    private bool IsExpired(CachedPayloadDocument document) =>
        _timeProvider.GetUtcNow() - document.CachedAtUtc >= _options.Ttl;

    private async Task WriteEntryAsync(
        string entryDirectory,
        CachedPayloadDocument document,
        CancellationToken cancellationToken)
    {
        // Write-then-rename: a reader either sees the previous entry.json or the complete new one,
        // never a truncated file mid-write.
        var entryPath = Path.Combine(entryDirectory, EntryFileName);
        var temporaryPath = entryPath + ".tmp";

        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(document, JsonOptions),
            cancellationToken);

        File.Move(temporaryPath, entryPath, overwrite: true);
    }

    private CachedPayloadDocument? TryReadDocument(string entryDirectory)
    {
        try
        {
            var entryPath = Path.Combine(entryDirectory, EntryFileName);
            return File.Exists(entryPath)
                ? JsonSerializer.Deserialize<CachedPayloadDocument>(File.ReadAllText(entryPath), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFiles().Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private bool DeleteEntry(string entryDirectory)
    {
        try
        {
            if (!Directory.Exists(entryDirectory))
            {
                return false;
            }

            Directory.Delete(entryDirectory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Usually an upload still streaming from this very entry. The next sweep gets it.
            _logger.LogDebug(ex, "Could not delete cache entry {Directory}", entryDirectory);
            return false;
        }
    }

    /// <summary>
    /// The source file's extension, reduced to something that can't escape the entry directory or
    /// upset the filesystem. Extractors name files from platform metadata, so this is untrusted input.
    /// </summary>
    private static string SafeExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Length is <= 1 or > MaxExtensionLength)
        {
            return ".bin";
        }

        return extension[1..].All(char.IsLetterOrDigit) ? extension.ToLowerInvariant() : ".bin";
    }
}
