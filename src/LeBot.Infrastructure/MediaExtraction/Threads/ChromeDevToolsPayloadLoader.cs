using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// Loads a Threads post's payload block in a headless Chromium browser (system Chrome, then Edge).
/// It speaks the Chrome DevTools Protocol directly over a WebSocket — no Playwright/Selenium driver
/// to package — so it stays compatible with the single-file, self-contained deployment (Playwright's
/// driver isn't found under single-file publish; see ADR 0006). The browser is a <em>runtime</em>
/// prerequisite, not shipped: when none is present the loader returns <c>null</c> and the caller
/// falls back to the og:image card, as it did before any browser path existed.
/// </summary>
internal sealed class ChromeDevToolsPayloadLoader : IBrowserPayloadLoader, IDisposable
{
    // Searched in order when Threads:BrowserPath is unset. Chrome first, then Edge (always present
    // on Windows 11) so the feature works out of the box even without Chrome installed.
    private static readonly string[] CandidatePaths =
    [
        @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
        @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
        @"%LocalAppData%\Google\Chrome\Application\chrome.exe",
        @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
        @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",
    ];

    private static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0.0.0 Safari/537.36";

    // This path is the exception, not the rule — one browser at a time keeps memory bounded even
    // when several links land together.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ThreadsOptions _options;
    private readonly ILogger<ChromeDevToolsPayloadLoader> _logger;
    private readonly Lazy<string?> _browserPath;

    public ChromeDevToolsPayloadLoader(
        IOptions<ThreadsOptions> options,
        ILogger<ChromeDevToolsPayloadLoader> logger)
    {
        _options = options.Value;
        _logger = logger;
        _browserPath = new Lazy<string?>(ResolveBrowserPath);
    }

    public async Task<string?> LoadPostPayloadAsync(Uri pageUrl, string shortcode, CancellationToken cancellationToken)
    {
        var browser = _browserPath.Value;
        if (browser is null)
        {
            _logger.LogWarning(
                "No Chromium browser found for the Threads fallback (set Threads:BrowserPath). Falling back to the og:image card.");
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.PageTimeoutSeconds));
            return await RunAsync(browser, pageUrl, shortcode, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Threads payload load timed out after {Seconds}s for {Host}",
                _options.PageTimeoutSeconds, pageUrl.Host);
            return null;
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException or IOException
            or System.ComponentModel.Win32Exception or HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Threads headless load failed for {Host}; falling back to the og:image card", pageUrl.Host);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> RunAsync(string browserPath, Uri pageUrl, string shortcode, CancellationToken ct)
    {
        var profileDir = Path.Combine(Path.GetTempPath(), "lebot-cdp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profileDir);
        Process? process = null;
        try
        {
            process = LaunchBrowser(browserPath, profileDir);

            var port = await ReadDebugPortAsync(profileDir, ct);
            if (port == 0)
            {
                _logger.LogWarning("Headless browser never reported a DevTools port; falling back to thumbnail");
                return null;
            }

            var wsUrl = await GetBrowserWebSocketUrlAsync(port, ct);
            await using var cdp = await CdpConnection.ConnectAsync(wsUrl, ct);

            var sessionId = await cdp.OpenPageAsync(ct);
            await cdp.SendAsync("Page.navigate", new { url = pageUrl.AbsoluteUri }, sessionId, ct);

            return await PollForPayloadAsync(cdp, sessionId, shortcode, ct);
        }
        finally
        {
            KillQuietly(process);
            TryDeleteDirectory(profileDir);
        }
    }

    private Process LaunchBrowser(string browserPath, string profileDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = browserPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "--headless=new",
            "--disable-gpu",
            "--remote-debugging-port=0",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--disable-background-networking",
            "--mute-audio",
            "--window-size=1280,1400",
            $"--user-agent={BrowserUserAgent}",
            $"--user-data-dir={profileDir}",
            "about:blank",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        return Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start headless browser at {browserPath}");
    }

    // Chrome writes the chosen port to DevToolsActivePort (line 1) once the debug server is up.
    private static async Task<int> ReadDebugPortAsync(string profileDir, CancellationToken ct)
    {
        var portFile = Path.Combine(profileDir, "DevToolsActivePort");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(portFile))
            {
                try
                {
                    var first = (await File.ReadAllLinesAsync(portFile, ct)).FirstOrDefault();
                    if (int.TryParse(first, out var port))
                    {
                        return port;
                    }
                }
                catch (IOException)
                {
                    // Chrome may still be writing the file; retry.
                }
            }

            await Task.Delay(100, ct);
        }
    }

    private static async Task<string> GetBrowserWebSocketUrlAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
            ?? throw new InvalidOperationException("DevTools /json/version carried no webSocketDebuggerUrl");
    }

    private async Task<string?> PollForPayloadAsync(
        CdpConnection cdp,
        string sessionId,
        string shortcode,
        CancellationToken ct)
    {
        // The payload lands a beat after navigation, so poll until a block describes *this* post.
        // Matching on the post's own code is what keeps a neighbour's media — the recommendation
        // feed further down the page carries plenty — out of the answer. The code is validated as
        // base64url by ThreadsUrl.Shortcode before it reaches this string.
        var expression = $$"""
            (() => {
              for (const s of document.querySelectorAll('script[type="application/json"]')) {
                const t = s.textContent || '';
                if (t.includes('"code":"{{shortcode}}"')
                    && (t.includes('image_versions2') || t.includes('video_versions'))) {
                  return t;
                }
              }
              return '';
            })()
            """;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var result = await cdp.SendAsync(
                "Runtime.evaluate",
                new { expression, returnByValue = true },
                sessionId,
                ct);

            var value = result
                .GetProperty("result").GetProperty("result").GetProperty("value").GetString();

            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(400, ct);
        }
    }

    private string? ResolveBrowserPath()
    {
        if (!_options.BrowserFallbackEnabled)
        {
            _logger.LogInformation("Threads headless fallback disabled via Threads:BrowserFallbackEnabled");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_options.BrowserPath))
        {
            return File.Exists(_options.BrowserPath) ? _options.BrowserPath : null;
        }

        foreach (var candidate in CandidatePaths)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded))
            {
                _logger.LogInformation("Threads browser fallback using {Browser}", expanded);
                return expanded;
            }
        }

        return null;
    }

    private void KillQuietly(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogDebug(ex, "Could not kill headless browser process cleanly");
        }
        finally
        {
            process.Dispose();
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete temp browser profile {Path}", path);
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Minimal Chrome DevTools Protocol client over a single WebSocket: correlates command
    /// responses by id and runs page commands through a flattened session.
    /// </summary>
    private sealed class CdpConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _receiveCts = new();
        private readonly Task _receiveLoop;
        private int _nextId;

        private CdpConnection(ClientWebSocket socket)
        {
            _socket = socket;
            _receiveLoop = Task.Run(ReceiveLoopAsync);
        }

        public static async Task<CdpConnection> ConnectAsync(string wsUrl, CancellationToken ct)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(wsUrl), ct);
            return new CdpConnection(socket);
        }

        /// <summary>Creates a blank tab, attaches to it (flattened), and returns the session id.</summary>
        public async Task<string> OpenPageAsync(CancellationToken ct)
        {
            var created = await SendAsync("Target.createTarget", new { url = "about:blank" }, sessionId: null, ct);
            var targetId = created.GetProperty("result").GetProperty("targetId").GetString();
            var attached = await SendAsync("Target.attachToTarget", new { targetId, flatten = true }, sessionId: null, ct);
            return attached.GetProperty("result").GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException("Target.attachToTarget returned no sessionId");
        }

        public async Task<JsonElement> SendAsync(string method, object? @params, string? sessionId, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var message = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
            if (@params is not null)
            {
                message["params"] = @params;
            }

            if (sessionId is not null)
            {
                message["sessionId"] = sessionId;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await _sendLock.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            finally
            {
                _sendLock.Release();
            }

            await using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                return await tcs.Task;
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            var builder = new StringBuilder();
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    builder.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, _receiveCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    Dispatch(builder.ToString());
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // Connection torn down (disposal or browser exit); fail any in-flight commands below.
            }
            finally
            {
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetException(new InvalidOperationException("CDP connection closed"));
                }
            }
        }

        private void Dispatch(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement)
                && _pending.TryRemove(idElement.GetInt32(), out var tcs))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    tcs.TrySetException(new InvalidOperationException(
                        $"CDP error: {error.GetRawText()}"));
                }
                else
                {
                    tcs.TrySetResult(root.Clone());
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _receiveCts.CancelAsync();
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Best-effort close.
            }

            _socket.Dispose();
            _sendLock.Dispose();
            _receiveCts.Dispose();
        }
    }
}
