using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace Sherland.Aspire.Testing.Xunit;

/// <summary>
/// Base class for xUnit fixtures that start an Aspire <see cref="DistributedApplication"/>
/// and need automatic diagnostics (resource state, logs, browser console) printed to the
/// test console when a test fails.
///
/// Usage:
/// <code>
/// public sealed class MyFixture : AspireFixtureBase
/// {
///     private DistributedApplication? _app;
///     internal DistributedApplication App => _app!;
///
///     public override async Task InitializeAsync()
///     {
///         var appHost = await DistributedApplicationTestingBuilder
///             .CreateAsync&lt;Projects.MyAppHost&gt;(...);
///         _app = await appHost.BuildAsync();
///         await _app.StartAsync();
///
///         BeginWatching(_app, "api", "frontend");
///     }
///
///     public override async Task DisposeAsync()
///     {
///         await base.DisposeAsync();   // stops background watchers
///         if (_app is not null) await _app.DisposeAsync();
///     }
/// }
/// </code>
/// </summary>
public abstract class AspireFixtureBase : IAsyncLifetime, IAspireTestDiagnosticsProvider
{
    private DistributedApplication? _watchedApp;
    private ResourceLoggerService? _resourceLoggerService;
    private CancellationTokenSource? _diagnosticsCts;
    private Task? _resourceNotificationTask;
    private readonly BufferingLoggerProvider _appHostLogProvider = new();
    private readonly ConcurrentDictionary<string, RingBuffer<string>> _resourceLogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ResourceEvent> _resourceStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _watchedResources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<Task> _resourceLogTasks = [];
    private OtelTraceCapture? _traceCapture;
    private string? _telemetryCaptureSkipReason;

    /// <summary>
    /// How long to wait after test failure before querying the dashboard's telemetry API,
    /// to allow in-flight OTEL data to be flushed and stored by the dashboard.
    /// Only used when <see cref="EnableTraceCapture"/> has been called.
    /// Default: 1 second.
    /// </summary>
    protected TimeSpan TelemetryFlushDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Category name prefixes excluded from the AppHost log buffer.
    /// Any logger whose category starts with one of these strings will be silently dropped
    /// — no level change, just not buffered. Useful for categories that are already captured
    /// via OTEL and would otherwise produce duplicate output on test failure.
    /// <para>
    /// <c>System.Net.Http.HttpClient</c> is excluded by default (Aspire health-check pings).
    /// Add your own app-specific prefixes before calling <c>BuildAsync()</c>, for example:
    /// <code>BufferingLogExcludedPrefixes.Add("MyApp.AppHost.Resources.");</code>
    /// </para>
    /// </summary>
    protected IList<string> BufferingLogExcludedPrefixes { get; } =
        ["System.Net.Http.HttpClient"];

    /// <summary>
    /// Pass to <c>appHost.Services.AddLogging(BufferingLogging)</c> before <c>BuildAsync()</c>
    /// to capture AppHost log output in the failure-diagnostics buffer instead of writing
    /// it directly to the console.
    /// </summary>
    protected Action<ILoggingBuilder> BufferingLogging =>
        logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(_appHostLogProvider);
            foreach (var prefix in BufferingLogExcludedPrefixes)
                logging.AddFilter(prefix, LogLevel.None);
        };

    // -------------------------------------------------------------------------
    // Abstract lifecycle — subclass must implement both
    // -------------------------------------------------------------------------

    public abstract ValueTask InitializeAsync();

    // -------------------------------------------------------------------------
    // Protected helpers for subclasses
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves the Aspire dashboard's REST telemetry API and returns an
    /// <see cref="OtelTraceCapture"/> that subclasses can expose to tests for in-test trace
    /// assertions. The same instance is also used automatically for failure diagnostics
    /// (span/log counts and OTLP JSON artifacts appended to the failure output). Call this
    /// after <see cref="BeginWatching"/> once the <see cref="DistributedApplication"/> is running.
    /// Returns <c>null</c> when the dashboard is not available (e.g. <c>DisableDashboard</c>
    /// was not set to <c>false</c>) — the skip reason is still recorded for the diagnostics block.
    /// </summary>
    protected OtelTraceCapture? EnableTraceCapture(DistributedApplication app)
    {
        if (OtelTraceCapture.TryCreate(app, out var capture, out var skipReason))
        {
            _traceCapture = capture;
            return capture;
        }

        _telemetryCaptureSkipReason = skipReason;
        return null;
    }

    /// <summary>
    /// Stops the background watchers then returns. Subclass should call
    /// <c>await base.DisposeAsync()</c> before disposing its own resources.
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        await StopWatchingAsync();
        if (_traceCapture is not null)
            await _traceCapture.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // Protected helpers for subclasses
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts the background resource-state and resource-log watchers.
    /// Call this after the <see cref="DistributedApplication"/> has been started.
    /// </summary>
    /// <param name="app">The running distributed application.</param>
    /// <param name="initialResources">
    /// Resource names to begin watching immediately (e.g. "api", "frontend").
    /// Additional resources discovered at runtime via notifications are watched automatically.
    /// </param>
    protected void BeginWatching(DistributedApplication app, params string[] initialResources)
    {
        _watchedApp = app;
        _resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        _diagnosticsCts = new CancellationTokenSource();

        foreach (var name in initialResources)
        {
            EnsureResourceWatcher(name);
        }

        _resourceNotificationTask = Task.Run(
            () => WatchResourceNotificationsAsync(_diagnosticsCts.Token),
            _diagnosticsCts.Token);
    }

    // -------------------------------------------------------------------------
    // Lifecycle helpers (internal)
    // -------------------------------------------------------------------------
    /// <summary>
    /// Cancels the background watchers and awaits their completion.
    /// Called automatically by <see cref="DisposeAsync"/>.
    /// </summary>
    protected async Task StopWatchingAsync()
    {
        if (_diagnosticsCts is not null)
        {
            await _diagnosticsCts.CancelAsync();
        }

        if (_resourceNotificationTask is not null)
        {
            await IgnoreCancellationAsync(_resourceNotificationTask);
        }

        foreach (var task in _resourceLogTasks)
        {
            await IgnoreCancellationAsync(task);
        }

        _diagnosticsCts?.Dispose();
    }

    // -------------------------------------------------------------------------
    // IAspireTestDiagnosticsProvider
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes a diagnostics block containing:
    /// <list type="bullet">
    ///   <item>The latest state and health of every known resource.</item>
    ///   <item>The most recent log lines from each resource (capped ring buffer).</item>
    ///   <item>Browser console messages and page errors (when a Playwright page was tracked).</item>
    /// </list>
    /// This is called automatically by the xUnit runner on test failure and routes output
    /// through xUnit's message bus so it is associated with the failing test.
    /// </summary>
    public async ValueTask WriteDiagnosticsAsync(string testName, BrowserDiagnosticsScope? browserScope, Action<string> writeLine)
    {
        var output = new StringBuilder()
            .AppendLine()
            .AppendLine($"========== ASPIRE DIAGNOSTICS: {testName} ==========")
            .AppendLine("Resource states:");

        foreach (var entry in _resourceStates.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = entry.Value.Snapshot;
            output.Append("  - ")
                .Append(entry.Key)
                .Append(": state=")
                .Append(snapshot.State?.Text ?? "<unknown>")
                .Append(", health=")
                .Append(snapshot.HealthStatus?.ToString() ?? "<unknown>");

            if (snapshot.ExitCode is not null)
            {
                output.Append(", exit_code=").Append(snapshot.ExitCode.Value);
            }

            output.AppendLine();
        }

        foreach (var entry in _resourceLogs.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var lines = entry.Value.Snapshot();
            if (lines.Count == 0) continue;

            output.AppendLine()
                .Append("Recent resource output [")
                .Append(entry.Key)
                .AppendLine("]:");

            foreach (var line in lines)
            {
                output.Append("  ").AppendLine(line);
            }
        }

        if (browserScope is not null && browserScope.Entries.Count > 0)
        {
            output.AppendLine()
                .AppendLine("Recent browser console / trace output:");

            foreach (var line in browserScope.Entries)
            {
                output.Append("  ").AppendLine(line);
            }
        }

        var appHostLogs = _appHostLogProvider.Snapshot();
        if (appHostLogs.Count > 0)
        {
            output.AppendLine()
                .AppendLine("AppHost log output:");

            foreach (var line in appHostLogs)
            {
                output.Append("  ").AppendLine(line);
            }
        }

        // Flush OTEL data before querying the dashboard — brief wait for in-flight exports.
        if (_traceCapture is not null)
        {
            await Task.Delay(TelemetryFlushDelay);

            TelemetryCaptureResult? result = null;
            try
            {
                result = await _traceCapture.CaptureFailureDiagnosticsAsync(
                    testName,
                    AspireTestTimingContext.TestStartTime);
            }
            catch (Exception ex)
            {
                output.AppendLine()
                    .AppendLine("OTEL telemetry: capture failed — " + ex.Message);
            }

            if (result is not null)
            {
                output.AppendLine()
                    .AppendLine("OTEL telemetry (this test / total stored in dashboard):");

                output.Append("  Spans : ")
                    .Append(result.SpansInWindow)
                    .Append(" in test window / ")
                    .Append(result.TotalSpans)
                    .AppendLine(" total");

                output.Append("  Logs  : ")
                    .Append(result.LogsInWindow)
                    .Append(" in test window / ")
                    .Append(result.TotalLogs)
                    .AppendLine(" total");

                output.AppendLine()
                    .AppendLine("  Artefact files (OTLP JSON):");
                output.Append("    Spans : ").AppendLine(result.SpansFilePath);
                output.Append("    Logs  : ").AppendLine(result.LogsFilePath);
            }
        }
        else if (_telemetryCaptureSkipReason is not null)
        {
            output.AppendLine()
                .AppendLine("OTEL telemetry: skipped — " + _telemetryCaptureSkipReason);
        }

        output.AppendLine("======== END ASPIRE DIAGNOSTICS ========");

        writeLine(output.ToString());
    }

    // -------------------------------------------------------------------------
    // Background watchers
    // -------------------------------------------------------------------------

    private async Task WatchResourceNotificationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var resourceEvent in _watchedApp!.ResourceNotifications.WatchAsync(cancellationToken))
            {
                _resourceStates[resourceEvent.Resource.Name] = resourceEvent;
                EnsureResourceWatcher(resourceEvent.Resource.Name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void EnsureResourceWatcher(string resourceName)
    {
        if (_resourceLoggerService is null || _diagnosticsCts is null)
        {
            return;
        }

        if (!_watchedResources.TryAdd(resourceName, 0))
        {
            return;
        }

        _resourceLogTasks.Add(Task.Run(
            () => WatchResourceLogsAsync(resourceName, _diagnosticsCts.Token),
            _diagnosticsCts.Token));
    }

    private async Task WatchResourceLogsAsync(string resourceName, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batch in _resourceLoggerService!.GetAllAsync(resourceName).WithCancellation(cancellationToken))
            {
                AppendLogBatch(resourceName, batch);
            }

            await foreach (var batch in _resourceLoggerService.WatchAsync(resourceName).WithCancellation(cancellationToken))
            {
                AppendLogBatch(resourceName, batch);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void AppendLogBatch(string resourceName, IReadOnlyList<LogLine> batch)
    {
        var buffer = _resourceLogs.GetOrAdd(resourceName, _ => new RingBuffer<string>(80));

        foreach (var line in batch)
        {
            var level = line.IsErrorMessage ? "ERR" : "OUT";
            buffer.Add($"{line.LineNumber,4} [{level}] {line.Content}");
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

