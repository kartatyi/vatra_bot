using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.MediaExtraction.Instagram;

/// <summary>
/// Supplies Instagram session cookies for the private web API, sourced from the browser named in
/// <see cref="YtDlpOptions.CookiesFromBrowser"/> via <see cref="IBrowserCookieJarReader"/>. The
/// session is read at most once per <see cref="CacheTtl"/> (exporting a jar spawns a process and a
/// logged-in session is stable for weeks); a 401/403 from the API drives a forced refresh.
/// </summary>
internal sealed class YtDlpCookieProvider : IInstagramCookieProvider, IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private readonly IBrowserCookieJarReader _jarReader;
    private readonly YtDlpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<YtDlpCookieProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private InstagramCookies? _cached;
    private DateTimeOffset _cachedAtUtc;

    public YtDlpCookieProvider(
        IBrowserCookieJarReader jarReader,
        IOptions<YtDlpOptions> options,
        TimeProvider timeProvider,
        ILogger<YtDlpCookieProvider> logger)
    {
        _jarReader = jarReader;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<InstagramCookies?> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var browser = _options.CookiesFromBrowser;
        if (string.IsNullOrEmpty(browser))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (!forceRefresh && _cached is not null && now - _cachedAtUtc < CacheTtl)
            {
                return _cached;
            }

            var jar = await _jarReader.ReadAsync(browser, cancellationToken);
            var fresh = jar is null ? null : ParseJar(jar);
            if (fresh is not null)
            {
                _cached = fresh;
                _cachedAtUtc = now;
                return fresh;
            }

            if (jar is not null)
            {
                _logger.LogWarning(
                    "Cookie jar from {Browser} carried no instagram.com sessionid — is that browser logged into Instagram?",
                    browser);
            }

            // The export failed or had no sessionid. Prefer a stale session over nothing — it may
            // still be valid, and the caller refreshes again on the next 401.
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Parses a Netscape-format cookie jar, returning the instagram.com session cookies, or
    /// <c>null</c> when no <c>sessionid</c> is present. Internal for unit testing.
    /// </summary>
    internal static InstagramCookies? ParseJar(IEnumerable<string> jarLines)
    {
        string? sessionId = null;
        string? dsUserId = null;
        string? csrfToken = null;

        foreach (var rawLine in jarLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            // sessionid is HttpOnly; some exporters prefix such rows with "#HttpOnly_" rather than
            // emitting a plain line. Strip that marker; treat every other '#' line as a comment.
            var line = rawLine;
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal))
            {
                line = line["#HttpOnly_".Length..];
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 7 || !IsInstagramDomain(fields[0]))
            {
                continue;
            }

            switch (fields[5])
            {
                case "sessionid":
                    sessionId = fields[6];
                    break;
                case "ds_user_id":
                    dsUserId = fields[6];
                    break;
                case "csrftoken":
                    csrfToken = fields[6];
                    break;
            }
        }

        return string.IsNullOrEmpty(sessionId)
            ? null
            : new InstagramCookies(sessionId, dsUserId, csrfToken);
    }

    // Match instagram.com and its subdomains exactly — never a substring, so look-alike domains
    // like "notinstagram.com" or "instagram.com.evil.example" in the dumped jar can't smuggle a
    // foreign sessionid into the Instagram request.
    private static bool IsInstagramDomain(string cookieDomain)
    {
        var host = cookieDomain.TrimStart('.');
        return host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _gate.Dispose();
}
