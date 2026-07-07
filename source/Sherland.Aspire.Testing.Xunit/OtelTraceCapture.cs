using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sherland.Aspire.Testing.Xunit;

/// <summary>
/// Verifies OTEL trace spans produced by an Aspire app under test — whether the spans
/// originate from direct backend API calls or from browser-driven interactions.
/// Backend-originated spans are observed by polling the Aspire dashboard's REST telemetry
/// API (<c>/api/telemetry/spans</c>). Browser-originated spans are additionally observed by
/// intercepting the page's outgoing OTLP export directly (<see cref="AttachBrowserCaptureAsync"/>)
/// rather than relying solely on that export reaching the dashboard — the dashboard's OTLP/HTTP
/// endpoint is dynamically allocated and not always reachable/CORS-permitted from a test-hosted
/// browser page. Both sources feed the same <see cref="Spans"/>/<see cref="RootSpans"/> collections.
///
/// Constructed by <see cref="AspireFixtureBase.EnableTraceCapture"/>. Also used internally
/// for automatic failure diagnostics (span/log counts and OTLP JSON artifacts) via
/// <see cref="CaptureFailureDiagnosticsAsync"/>.
///
/// Usage:
/// <code>
/// fixture.Traces.Reset();
///
/// using var client = fixture.App.CreateHttpClient("demo-api");
/// await client.PostAsync("/items/3/process", content: null);
///
/// await fixture.Traces.WaitForAsync(spans => spans.Any(s => s.IsRoot));
/// fixture.Traces.AssertRootSpanCount(1);
/// </code>
/// </summary>
public sealed class OtelTraceCapture : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly object _lock = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private List<CapturedSpan> _lastSnapshot = [];
    private readonly List<CapturedSpan> _browserSpans = [];

    private static readonly JsonSerializerOptions _jsonWriteOptions = new()
    {
        WriteIndented = true,
    };

    private OtelTraceCapture(HttpClient client)
    {
        _client = client;
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves the Aspire dashboard's REST API URL and API key from the running
    /// <see cref="DistributedApplication"/> and builds a configured <see cref="OtelTraceCapture"/>.
    /// Returns <c>false</c> with a <paramref name="skipReason"/> when the dashboard is not
    /// available (e.g. <c>DisableDashboard</c> was not set to <c>false</c>).
    /// HTTPS certificate errors are suppressed because the Aspire dashboard in a testing
    /// environment uses a self-signed or dev certificate.
    /// </summary>
    internal static bool TryCreate(DistributedApplication app, out OtelTraceCapture? capture, out string? skipReason)
    {
        capture = null;
        skipReason = null;

        try
        {
            var model = app.Services.GetRequiredService<DistributedApplicationModel>();
            var dashboard = model.Resources
                .OfType<IResourceWithEndpoints>()
                .FirstOrDefault(r => r.Name.Equals("aspire-dashboard", StringComparison.OrdinalIgnoreCase));

            if (dashboard is null)
            {
                skipReason =
                    "aspire-dashboard resource not found in DistributedApplicationModel. " +
                    "Set appOptions.DisableDashboard = false before BuildAsync().";
                return false;
            }

            // The dashboard frontend and API share the same port.
            // Prefer the 'http' endpoint; fall back to first available.
            var ep = dashboard.Annotations
                .OfType<EndpointAnnotation>()
                .FirstOrDefault(a => a.Name.Equals("http", StringComparison.OrdinalIgnoreCase))
                ?? dashboard.Annotations
                    .OfType<EndpointAnnotation>()
                    .FirstOrDefault();

            var dashboardUrl = ep?.AllocatedEndpoint?.UriString?.TrimEnd('/');

            if (string.IsNullOrEmpty(dashboardUrl))
            {
                var endpoints = string.Join(", ", dashboard.Annotations
                    .OfType<EndpointAnnotation>()
                    .Select(a => $"{a.Name}: allocated={a.AllocatedEndpoint?.UriString ?? "null"}"));
                skipReason = $"Dashboard AllocatedEndpoint is null. Endpoints: [{endpoints}]";
                return false;
            }

            var config = app.Services.GetRequiredService<IConfiguration>();
            var apiKey = config["Dashboard:Api:PrimaryApiKey"];

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            var client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri(dashboardUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            }

            capture = new OtelTraceCapture(client);
            return true;
        }
        catch (Exception ex)
        {
            skipReason = $"Dashboard URL resolution failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Live capture / assertion API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Marks the start of a new assertion window (now) and clears the last-fetched snapshot.
    /// Call this immediately before the action under test (an API call, a browser interaction,
    /// or both).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _windowStart = DateTimeOffset.UtcNow;
            _lastSnapshot = [];
            _browserSpans.Clear();
        }
    }

    /// <summary>
    /// All spans observed so far in the current window (since <see cref="Reset"/>) — merging
    /// the dashboard's REST API snapshot (backend-originated spans) with any spans captured via
    /// <see cref="AttachBrowserCaptureAsync"/> (browser-originated spans), deduplicated by span ID.
    /// </summary>
    public IReadOnlyList<CapturedSpan> Spans
    {
        get
        {
            lock (_lock)
            {
                return [.. _lastSnapshot
                    .Concat(_browserSpans)
                    .Where(s => s.StartTime >= _windowStart)
                    .DistinctBy(s => s.SpanId)];
            }
        }
    }

    /// <summary>All root spans (no parent span ID) within the current window.</summary>
    public IReadOnlyList<CapturedSpan> RootSpans => [.. Spans.Where(s => s.IsRoot)];

    /// <summary>
    /// Polls the Aspire dashboard's <c>/api/telemetry/spans</c> REST endpoint once and
    /// updates the last-fetched snapshot backing <see cref="Spans"/>/<see cref="RootSpans"/>.
    /// </summary>
    public async Task RefreshAsync(int limit = 1000, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync($"/api/telemetry/spans?limit={limit}", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return;

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("data", out var data))
            return;

        var parsed = OtlpSpanParser.ParseSpansDocument(data);

        lock (_lock)
        {
            _lastSnapshot = parsed;
        }
    }

    /// <summary>
    /// Polls <see cref="RefreshAsync"/> on an interval until <paramref name="condition"/>
    /// is satisfied by the current windowed <see cref="Spans"/>, or the timeout elapses.
    /// Because dashboard ingestion is asynchronous, assertions should always be preceded
    /// by a <see cref="WaitForAsync"/> call rather than a single <see cref="RefreshAsync"/>.
    /// </summary>
    /// <exception cref="TimeoutException">The condition was not met within the timeout.</exception>
    public async Task WaitForAsync(
        Func<IReadOnlyList<CapturedSpan>, bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var effectiveInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;

        while (true)
        {
            await RefreshAsync(cancellationToken: cancellationToken);
            var snapshot = Spans;

            if (condition(snapshot))
                return;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Condition not met within {effectiveTimeout.TotalSeconds:F1}s. " +
                    $"Last snapshot ({snapshot.Count} span(s)): " +
                    string.Join(", ", snapshot.Select(s => $"\"{s.Name}\"")));
            }

            await Task.Delay(effectiveInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Optional latency-reduction helper: forces the browser's <c>BatchSpanProcessor</c> to
    /// flush by dispatching a <c>beforeunload</c> event on <paramref name="page"/>, then waits
    /// until an OTLP export response arrives (or the timeout elapses), so a subsequent
    /// <see cref="WaitForAsync"/> call needs fewer polling rounds. Verification never depends
    /// on this being called — it only shaves latency.
    /// </summary>
    public async Task TriggerBrowserFlushAsync(IPage page, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);

        var waitTask = page.WaitForResponseAsync(
            r => r.Url.Contains("/v1/traces"),
            new PageWaitForResponseOptions { Timeout = (float)effectiveTimeout.TotalMilliseconds });

        await page.EvaluateAsync("() => window.dispatchEvent(new Event('beforeunload'))");

        try
        {
            await waitTask;
        }
        catch (TimeoutException)
        {
            // No export arrived — spans may already have been collected before this was
            // called. Not fatal; callers needing a strict guarantee should increase the timeout.
        }
    }

    /// <summary>
    /// Intercepts the browser's outgoing OTLP span export on <paramref name="page"/> and merges
    /// captured spans into <see cref="Spans"/>/<see cref="RootSpans"/> alongside the dashboard-polled
    /// backend spans. Unlike the dashboard poll, this observes the outgoing request directly —
    /// it doesn't depend on the browser's export actually reaching a reachable dashboard endpoint,
    /// so it works regardless of the dashboard's dynamically-allocated OTLP/HTTP port or browser
    /// CORS restrictions. Dispose the returned handle (or let the <c>await using</c> scope end)
    /// to stop intercepting, typically at the end of the test that attached it.
    /// </summary>
    public async Task<IAsyncDisposable> AttachBrowserCaptureAsync(IPage page, string otlpRoutePattern = "**/v1/traces")
    {
        await page.RouteAsync(otlpRoutePattern, async route =>
        {
            var postData = route.Request.PostData;
            if (postData is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(postData);
                    var parsed = OtlpSpanParser.ParseSpansDocument(doc.RootElement);
                    lock (_lock)
                    {
                        _browserSpans.AddRange(parsed);
                    }
                }
                catch
                {
                    // Do not break the page on a parse error.
                }
            }

            await route.ContinueAsync();
        });

        return new BrowserRouteHandle(page, otlpRoutePattern);
    }

    private sealed class BrowserRouteHandle(IPage page, string routePattern) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await page.UnrouteAsync(routePattern);
    }

    // ── Assertion helpers ────────────────────────────────────────────────────

    /// <summary>Asserts that exactly <paramref name="expected"/> root spans match <paramref name="predicate"/> (or all root spans, if null).</summary>
    public void AssertRootSpanCount(int expected, Func<CapturedSpan, bool>? predicate = null)
    {
        var roots = RootSpans.Where(s => predicate is null || predicate(s)).ToList();

        if (roots.Count != expected)
        {
            var allRoots = RootSpans;
            throw new InvalidOperationException(
                $"Expected exactly {expected} root span(s){(predicate is null ? "" : " matching predicate")}, but found {roots.Count}.\n" +
                $"All root spans ({allRoots.Count}): {string.Join(", ", allRoots.Select(s => $"\"{s.Name}\""))}");
        }
    }

    /// <summary>Asserts that exactly one root span matches <paramref name="predicate"/>.</summary>
    public void AssertSingleRootSpan(Func<CapturedSpan, bool> predicate) => AssertRootSpanCount(1, predicate);

    /// <summary>
    /// Asserts that every span matching <paramref name="childPredicate"/> has <c>ParentSpanId</c>
    /// equal to the span ID of the single root span matching <paramref name="parentPredicate"/>.
    /// </summary>
    public void AssertAllDirectChildren(
        Func<CapturedSpan, bool> parentPredicate,
        Func<CapturedSpan, bool> childPredicate)
    {
        var all = Spans;
        var parents = all.Where(s => s.IsRoot && parentPredicate(s)).ToList();

        if (parents.Count != 1)
            throw new InvalidOperationException(
                $"AssertAllDirectChildren: expected exactly 1 parent span, found {parents.Count}. " +
                $"Parents: {string.Join(", ", parents.Select(s => $"\"{s.Name}\""))}");

        var parent = parents[0];
        var children = all.Where(childPredicate).ToList();

        foreach (var child in children)
        {
            if (child.ParentSpanId != parent.SpanId)
                throw new InvalidOperationException(
                    $"Span \"{child.Name}\" (parentSpanId={child.ParentSpanId ?? "null"}) " +
                    $"is not a direct child of \"{parent.Name}\" (spanId={parent.SpanId}).");
        }
    }

    /// <summary>
    /// Asserts that no span has a <c>ParentSpanId</c> referring to a span not present in the
    /// current window (i.e. no dangling references), optionally excluding spans matching <paramref name="exclude"/>.
    /// </summary>
    public void AssertNoOrphanedSpans(Func<CapturedSpan, bool>? exclude = null)
    {
        var all = Spans;
        var spanIds = all.Select(s => s.SpanId).ToHashSet();
        var orphans = all
            .Where(s => !s.IsRoot && !spanIds.Contains(s.ParentSpanId ?? ""))
            .Where(s => exclude is null || !exclude(s))
            .ToList();

        if (orphans.Count > 0)
            throw new InvalidOperationException(
                $"Found {orphans.Count} orphaned span(s) with unknown parentSpanId:\n" +
                string.Join("\n", orphans.Select(s => $"  \"{s.Name}\" parentSpanId={s.ParentSpanId}")));
    }

    /// <summary>Asserts that at least one span with <c>IsError</c> set matches <paramref name="predicate"/> (or any error span, if null).</summary>
    public void AssertHasErrorSpan(Func<CapturedSpan, bool>? predicate = null)
    {
        var matches = Spans.Where(s => s.IsError && (predicate is null || predicate(s))).ToList();

        if (matches.Count == 0)
        {
            var all = Spans;
            throw new InvalidOperationException(
                $"Expected at least one error span{(predicate is null ? "" : " matching predicate")}, but found none.\n" +
                $"All spans ({all.Count}): {string.Join(", ", all.Select(s => $"\"{s.Name}\" (error={s.IsError})"))}");
        }
    }

    /// <summary>Asserts that no span in the current window has <c>IsError</c> set, optionally excluding spans matching <paramref name="exclude"/>.</summary>
    public void AssertNoErrorSpans(Func<CapturedSpan, bool>? exclude = null)
    {
        var errorSpans = Spans
            .Where(s => s.IsError)
            .Where(s => exclude is null || !exclude(s))
            .ToList();

        if (errorSpans.Count > 0)
            throw new InvalidOperationException(
                $"Found {errorSpans.Count} error span(s):\n" +
                string.Join("\n", errorSpans.Select(s => $"  \"{s.Name}\" status={s.StatusMessage}")));
    }

    // -------------------------------------------------------------------------
    // Failure diagnostics (internal — invoked by AspireFixtureBase.WriteDiagnosticsAsync)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches all spans and structured logs from the dashboard, writes OTLP JSON
    /// artefact files, and returns a summary. Used only for post-failure diagnostics —
    /// never affects a test's pass/fail outcome.
    /// </summary>
    internal async Task<TelemetryCaptureResult> CaptureFailureDiagnosticsAsync(
        string testName,
        DateTimeOffset testStartTime,
        CancellationToken cancellationToken = default)
    {
        var sanitized = SanitizeName(testName);
        var outputDir = Path.Combine(Path.GetTempPath(), "aspire-test-otel", sanitized);
        Directory.CreateDirectory(outputDir);

        var spansPath = Path.Combine(outputDir, "spans.otlp.json");
        var logsPath = Path.Combine(outputDir, "logs.otlp.json");

        var (spansData, totalSpans) = await FetchTelemetryJsonAsync(
            "/api/telemetry/spans?limit=1000", cancellationToken);

        var (logsData, totalLogs) = await FetchTelemetryJsonAsync(
            "/api/telemetry/logs?limit=1000", cancellationToken);

        await WriteOtlpJsonAsync(spansPath, spansData, cancellationToken);
        await WriteOtlpJsonAsync(logsPath, logsData, cancellationToken);

        var spansInWindow = CountSpansInWindow(spansData, testStartTime);
        var logsInWindow = CountLogsInWindow(logsData, testStartTime);

        return new TelemetryCaptureResult(
            TotalSpans: totalSpans,
            SpansInWindow: spansInWindow,
            TotalLogs: totalLogs,
            LogsInWindow: logsInWindow,
            SpansFilePath: spansPath,
            LogsFilePath: logsPath);
    }

    private async Task<(JsonNode? Data, int TotalCount)> FetchTelemetryJsonAsync(
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(relativeUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, 0);
            }

            var envelope = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
            if (envelope is null)
            {
                return (null, 0);
            }

            var data = envelope["data"];
            var totalCount = (int?)envelope["totalCount"] ?? 0;
            return (data, totalCount);
        }
        catch (Exception)
        {
            // Best-effort — never fail a test purely because diagnostics capture failed.
            return (null, 0);
        }
    }

    private static async Task WriteOtlpJsonAsync(
        string path,
        JsonNode? data,
        CancellationToken cancellationToken)
    {
        if (data is null)
        {
            await File.WriteAllTextAsync(path, "{}", cancellationToken);
            return;
        }

        using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data, _jsonWriteOptions, cancellationToken);
    }

    private static int CountSpansInWindow(JsonNode? spansData, DateTimeOffset testStartTime)
    {
        if (spansData is null)
        {
            return 0;
        }

        using var doc = JsonDocument.Parse(spansData.ToJsonString());
        // spansData is the 'data' array from the dashboard envelope — a resourceSpans array,
        // not an OTLP document root. Use ParseResourceSpans, not ParseSpansDocument.
        var spans = OtlpSpanParser.ParseResourceSpans(doc.RootElement);
        return spans.Count(s => s.StartTime >= testStartTime);
    }

    private static int CountLogsInWindow(JsonNode? logsData, DateTimeOffset testStartTime)
    {
        if (logsData is null)
        {
            return 0;
        }

        var count = 0;
        var thresholdNano = ToUnixNanoseconds(testStartTime);

        foreach (var resourceLogs in Iterate(logsData["resourceLogs"]))
        {
            foreach (var scopeLogs in Iterate(resourceLogs?["scopeLogs"]))
            {
                foreach (var logRecord in Iterate(scopeLogs?["logRecords"]))
                {
                    if (TryParseNano(logRecord?["timeUnixNano"]?.GetValue<string>(), out var nano)
                        && nano >= thresholdNano)
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    private static IEnumerable<JsonNode?> Iterate(JsonNode? array)
    {
        if (array is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                yield return item;
            }
        }
    }

    private static long ToUnixNanoseconds(DateTimeOffset dto)
    {
        return dto.ToUnixTimeMilliseconds() * 1_000_000L;
    }

    private static bool TryParseNano(string? value, out long nano)
    {
        if (value is not null && long.TryParse(value, out nano))
        {
            return true;
        }
        nano = 0;
        return false;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c))
                              .TrimEnd('.');
        return sanitized.Length > 80 ? sanitized[..80] : sanitized;
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Result of a failure-diagnostics capture operation. Counts are split into total (all stored
/// data in the dashboard) and in-window (entries whose timestamp falls after the test started).
/// </summary>
public sealed record TelemetryCaptureResult(
    int TotalSpans,
    int SpansInWindow,
    int TotalLogs,
    int LogsInWindow,
    string SpansFilePath,
    string LogsFilePath);
