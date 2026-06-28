using System.Net;
using System.Text.Json;
using LeBot.Application.Ports;
using LeBot.Application.Releases;
using LeBot.Domain.Common;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace LeBot.Infrastructure.Releases;

/// <summary>
/// Reads the latest release from the GitHub Releases API. Integrity comes from GitHub's
/// server-computed <c>asset.digest</c> (<c>sha256:…</c>) when present, falling back to a sibling
/// <c>&lt;asset&gt;.sha256</c> asset. Every expected failure maps to a <see cref="ReleaseSourceError"/>;
/// the method never throws for them.
/// </summary>
public sealed class GitHubReleaseSource(
    IHttpClientFactory httpClientFactory,
    IOptions<UpdateOptions> options,
    ILogger<GitHubReleaseSource> logger)
    : IReleaseSource
{
    private const string UserAgent = "LeBot-SelfUpdater";

    private readonly UpdateOptions _options = options.Value;

    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
                    .HandleResult(static r => IsTransientStatus(r.StatusCode)),
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(300),
                MaxDelay = TimeSpan.FromSeconds(5),
                UseJitter = true,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Retrying GitHub release HTTP call (attempt {Attempt} after {DelayMs}ms): {Reason}",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? $"status {args.Outcome.Result?.StatusCode}");
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout
        || status == HttpStatusCode.TooManyRequests
        || ((int)status >= 500 && (int)status < 600);

    public async Task<Result<ReleaseInfo, ReleaseSourceError>> GetLatestAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(nameof(GitHubReleaseSource));
        var endpoint = new Uri($"https://api.github.com/repos/{_options.Repository}/releases/latest");

        try
        {
            using var response = await _retryPipeline.ExecuteAsync(async token =>
            {
                var request = BuildApiRequest(HttpMethod.Get, endpoint);
                return await client.SendAsync(request, token);
            }, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("GitHub reports no releases for {Repository}", _options.Repository);
                return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.NoReleases);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "GitHub releases API returned HTTP {Status} for {Repository}",
                    (int)response.StatusCode, _options.Repository);
                return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.NetworkFailure);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return await ParseAsync(client, body, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "GitHub releases API request failed for {Repository}", _options.Repository);
            return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.NetworkFailure);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "GitHub releases API request timed out for {Repository}", _options.Repository);
            return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.NetworkFailure);
        }
    }

    private async Task<Result<ReleaseInfo, ReleaseSourceError>> ParseAsync(
        HttpClient client,
        string body,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "GitHub releases response was not valid JSON");
            return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.MalformedResponse);
        }

        using (document)
        {
            var root = document.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElement)
                || tagElement.ValueKind != JsonValueKind.String)
            {
                logger.LogWarning("GitHub release JSON had no tag_name");
                return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.MalformedResponse);
            }

            var tagName = tagElement.GetString();
            var versionResult = ReleaseVersion.Parse(tagName);
            if (versionResult is Result<ReleaseVersion, VersionParseError>.Err)
            {
                logger.LogWarning("GitHub release tag {Tag} is not a parseable version", tagName);
                return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.MalformedResponse);
            }

            var version = ((Result<ReleaseVersion, VersionParseError>.Ok)versionResult).Value;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.AssetMissing);
            }

            JsonElement? targetAsset = null;
            JsonElement? checksumAsset = null;
            var checksumAssetName = $"{_options.AssetName}.sha256";

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.Equals(name, _options.AssetName, StringComparison.Ordinal))
                {
                    targetAsset = asset;
                }
                else if (string.Equals(name, checksumAssetName, StringComparison.Ordinal))
                {
                    checksumAsset = asset;
                }
            }

            if (targetAsset is null)
            {
                logger.LogWarning(
                    "GitHub release {Tag} has no asset named {Asset}", tagName, _options.AssetName);
                return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.AssetMissing);
            }

            if (!TryGetDownloadUrl(targetAsset.Value, out var assetUrl))
            {
                return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.AssetMissing);
            }

            var checksum = await ResolveChecksumAsync(
                client, targetAsset.Value, checksumAsset, cancellationToken);
            if (checksum is null)
            {
                logger.LogWarning(
                    "GitHub release {Tag} has no usable SHA256 for {Asset}", tagName, _options.AssetName);
                return Result<ReleaseInfo, ReleaseSourceError>.Failure(ReleaseSourceError.ChecksumMissing);
            }

            var notes = root.TryGetProperty("body", out var bodyElement)
                && bodyElement.ValueKind == JsonValueKind.String
                    ? bodyElement.GetString()
                    : null;

            logger.LogDebug("GitHub latest release for {Repository}: {Tag}", _options.Repository, tagName);
            return Result<ReleaseInfo, ReleaseSourceError>.Success(
                new ReleaseInfo(version, assetUrl, checksum, tagName!, notes));
        }
    }

    private async Task<string?> ResolveChecksumAsync(
        HttpClient client,
        JsonElement targetAsset,
        JsonElement? checksumAsset,
        CancellationToken cancellationToken)
    {
        var fromDigest = ExtractDigest(targetAsset);
        if (fromDigest is not null)
        {
            return fromDigest;
        }

        if (checksumAsset is null || !TryGetDownloadUrl(checksumAsset.Value, out var checksumUrl))
        {
            return null;
        }

        try
        {
            using var response = await _retryPipeline.ExecuteAsync(async token =>
            {
                var request = BuildApiRequest(HttpMethod.Get, checksumUrl);
                return await client.SendAsync(request, token);
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Fetching {Url} returned HTTP {Status}", checksumUrl, (int)response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return ExtractLeadingHex(content);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to download checksum asset from {Url}", checksumUrl);
            return null;
        }
    }

    private static string? ExtractDigest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digestElement)
            || digestElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var digest = digestElement.GetString();
        if (string.IsNullOrEmpty(digest))
        {
            return null;
        }

        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ExtractLeadingHex(digest[prefix.Length..]);
    }

    private static string? ExtractLeadingHex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.TrimStart();
        var length = 0;
        while (length < trimmed.Length && Uri.IsHexDigit(trimmed[length]))
        {
            length++;
        }

        return length >= 64 ? trimmed[..64].ToLowerInvariant() : null;
    }

    private static bool TryGetDownloadUrl(JsonElement asset, out Uri url)
    {
        url = null!; // Set on every true return; callers only read it when true.
        if (!asset.TryGetProperty("browser_download_url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = urlElement.GetString();
        return Uri.TryCreate(raw, UriKind.Absolute, out url!);
    }

    private static HttpRequestMessage BuildApiRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }
}
