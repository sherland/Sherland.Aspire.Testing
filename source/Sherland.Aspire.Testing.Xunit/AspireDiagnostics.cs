using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using System.Threading;

namespace Sherland.Aspire.Testing.Xunit;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes log entries into a capped ring buffer
/// instead of directly to the console. Attach it to the AppHost's logging pipeline
/// so AppHost startup noise is buffered and only flushed to the console on test failure.
/// </summary>
public sealed class BufferingLoggerProvider(int capacity = 500) : ILoggerProvider
{
    private readonly RingBuffer<string> _buffer = new(capacity);

    public IReadOnlyList<string> Snapshot() => _buffer.Snapshot();

    public ILogger CreateLogger(string categoryName) => new BufferingLogger(categoryName, _buffer);

    public void Dispose() { }
}

file sealed class BufferingLogger(string categoryName, RingBuffer<string> buffer) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var level = logLevel switch
        {
            LogLevel.Trace       => "trce",
            LogLevel.Debug       => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning     => "warn",
            LogLevel.Error       => "fail",
            LogLevel.Critical    => "crit",
            _                    => "none",
        };
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{level}] {categoryName}: {message}";
        if (exception is not null) line += Environment.NewLine + exception;
        buffer.Add(line);
    }
}

/// <summary>
/// Attaches Playwright page event handlers to the active <see cref="BrowserDiagnosticsScope"/>
/// so browser console messages and page errors are captured and included in failure output.
///
/// Call <see cref="Track{T}"/> immediately after creating a Playwright page:
/// <code>
/// var page = AspirePageDiagnostics.Track(await _context.NewPageAsync());
/// </code>
/// </summary>
public static class AspirePageDiagnostics
{
    /// <summary>
    /// Attaches diagnostics event handlers to <paramref name="page"/> and returns it
    /// unchanged (to allow inline use). Does nothing if there is no active scope.
    /// </summary>
    public static T Track<T>(T page) where T : IPage
    {
        AspireDiagnosticsContext.Current?.Attach(page);
        return page;
    }
}

/// <summary>
/// Tracks the wall-clock time at which the currently executing test began.
/// Set by the xUnit runner at the top of each test's execution and read by
/// <see cref="AspireFixtureBase"/> when writing OTEL telemetry diagnostics.
/// </summary>
internal static class AspireTestTimingContext
{
    private static readonly AsyncLocal<DateTimeOffset> _start = new();

    /// <summary>
    /// Records the current UTC time as the start of the active test.
    /// Called by the runner before test body execution begins.
    /// </summary>
    internal static void BeginTest() => _start.Value = DateTimeOffset.UtcNow;

    /// <summary>
    /// Returns the start time of the active test, or <see cref="DateTimeOffset.MinValue"/>
    /// when called outside of a test execution context.
    /// </summary>
    internal static DateTimeOffset TestStartTime =>
        _start.Value == default ? DateTimeOffset.MinValue : _start.Value;
}

internal static class AspireDiagnosticsContext
{
    private static readonly AsyncLocal<BrowserDiagnosticsScope?> Scope = new();

    public static BrowserDiagnosticsScope? Current => Scope.Value;

    public static BrowserDiagnosticsContextScope BeginScope()
    {
        var scope = new BrowserDiagnosticsScope();
        Scope.Value = scope;
        return new BrowserDiagnosticsContextScope(scope);
    }

    internal static void Reset() => Scope.Value = null;
}

internal sealed class BrowserDiagnosticsContextScope(BrowserDiagnosticsScope browserScope) : IDisposable
{
    public BrowserDiagnosticsScope BrowserScope { get; } = browserScope;

    public void Dispose()
    {
        AspireDiagnosticsContext.Reset();
    }
}

/// <summary>
/// Collects browser console messages and page errors during a single test.
/// Populated via <see cref="AspirePageDiagnostics.Track{T}"/>.
/// </summary>
public sealed class BrowserDiagnosticsScope
{
    private readonly RingBuffer<string> _entries = new(120);

    public IReadOnlyList<string> Entries => _entries.Snapshot();

    internal void Attach(IPage page)
    {
        page.Console += (_, message) =>
        {
            Add($"[browser:{message.Type}] {message.Text}");
        };

        page.PageError += (_, message) =>
        {
            Add($"[browser:pageerror] {message}");
        };
    }

    private void Add(string line) => _entries.Add($"{DateTimeOffset.Now:HH:mm:ss.fff} {line}");
}

/// <summary>
/// A fixed-capacity circular buffer. When full, the oldest entry is evicted to make room.
/// Thread-safe.
/// </summary>
public sealed class RingBuffer<T>(int capacity)
{
    private readonly Queue<T> _items = new();
    private readonly Lock _lock = new();

    public void Add(T item)
    {
        lock (_lock)
        {
            if (_items.Count >= capacity)
            {
                _items.Dequeue();
            }

            _items.Enqueue(item);
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_lock)
        {
            return _items.ToArray();
        }
    }
}

