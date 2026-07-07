# Sherland.Aspire.Testing.Xunit — implementation internals

This document explains how the library works under the hood. Read it if you want to understand the extension points, modify the library, or satisfy your curiosity about how xUnit v3's pipeline connects to Aspire's resource-observation APIs.

For the user-facing quick-start guide, see [README.md](../README.md).

---

## The problem being solved

Running integration tests against a live Aspire stack is hard to debug. When a Playwright test fails with a timeout, the useful information is:

- Was the API actually running? Did it exit early with a non-zero exit code?
- What did the API log in the seconds before the failure?
- What did the browser console print?
- What did the AppHost's orchestration layer report?

Collecting and printing *all of this for every test* would bury passing-test output in megabytes of noise. Collecting *nothing* leaves you blind. The solution: continuously buffer diagnostics during every test and flush the buffer to the console **only when the test fails**.

---

## Architecture overview

```
┌──────────────────────────────────────────────────────────────────────────┐
│  xUnit v3 test pipeline                                                   │
│                                                                           │
│  [AspireFactAttribute]          ← test author writes this                │
│       │                                                                   │
│       │  names ──────────────► AspireFactDiscoverer                      │
│                                     │  returns                           │
│                                  AspireXunitTestCase                      │
│                                     │  ISelfExecutingXunitTestCase.Run() │
│                                  AspireTestCaseRunner                     │
│                                     │  RunTest() per test                │
│                                  AspireXunitTestRunner.Instance.Run()     │
│                                     │  OnTestFailed() hook               │
│                                  Diagnostics written to xUnit output      │
└──────────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────────┐
│  Aspire observation loop (background, for the lifetime of the fixture)   │
│                                                                           │
│  AppHostFixture.BeginWatching(app, ...)                                  │
│       ├── WatchResourceNotificationsAsync()  →  _resourceStates[name]   │
│       └── WatchResourceLogsAsync(name)       →  _resourceLogs[name]     │
│                                   (RingBuffer, capped, evicts oldest)    │
└──────────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────────┐
│  Browser event capture (per test, isolated via AsyncLocal)               │
│                                                                           │
│  AspireTestCaseRunner.RunTest() calls AspireDiagnosticsContext.BeginScope│
│       └── AsyncLocal<BrowserDiagnosticsScope> set for this async chain  │
│                                                                           │
│  AspirePageDiagnostics.Track(page)                                       │
│       └── page.Console / page.PageError → BrowserDiagnosticsScope.Add() │
│                                                                           │
│  OnTestFailed: scope retrieved via TestContext.Current.KeyValueStorage   │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## xUnit v3 concepts used

### `[Collection]` and `ICollectionFixture<T>` — shared setup

xUnit creates a new test class instance for each test method. Without collections, `AppHostFixture.InitializeAsync` would run once per class, and starting a full Aspire stack takes 10–30 seconds. That would make a 30-test suite take ten minutes.

**Collections** solve this. A collection definition registers a shared fixture type:

```csharp
[CollectionDefinition("AppTests")]
public class AppTestsCollection : ICollectionFixture<AppFixture> { }
```

xUnit creates **one** `AppFixture` instance, shared by every test class that declares `[Collection("AppTests")]`. The fixture lives from the first test in the collection to the last.

### `IAsyncLifetime` — async setup and teardown

xUnit v3's `IAsyncLifetime` uses `ValueTask` (not `Task` like v2):

```csharp
public interface IAsyncLifetime
{
    ValueTask InitializeAsync();   // runs before the first test in the class
    ValueTask DisposeAsync();      // runs after the last test in the class
}
```

`AspireFixtureBase` implements this to start and stop background watchers. Each Playwright test class also implements it to create and destroy the browser/context pair.

### `ISelfExecutingXunitTestCase` — owning the run

xUnit v3 introduced `ISelfExecutingXunitTestCase`. A test case that implements this interface is given full control over how it runs — instead of xUnit handing it to a framework-owned runner, it receives a `Run(...)` call and manages everything itself. This is the entry point for our custom pipeline.

### `TestContext.Current` — per-test state

xUnit v3 provides `TestContext.Current`, a per-test ambient context that is safe to access from any point in the execution pipeline. Its `KeyValueStorage` is a `ConcurrentDictionary<string, object>` that any layer can read/write. We use it to pass the `BrowserDiagnosticsContextScope` from `AspireTestCaseRunner.RunTest` to `AspireXunitTestRunner.OnTestFailed` without changing any method signatures.

---

## Layer-by-layer walkthrough

### Layer 1 — the attribute

```csharp
[XunitTestCaseDiscoverer(typeof(AspireFactDiscoverer))]
public sealed class AspireFactAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : FactAttribute(sourceFilePath, sourceLineNumber) { }
```

`[XunitTestCaseDiscoverer(typeof(T))]` is the xUnit v3 way of naming a discoverer — it takes a `Type` directly instead of a string + assembly name (the v2 pattern). The `[CallerFilePath]` / `[CallerLineNumber]` parameters are forwarded to `FactAttribute` so that IDE test-runner navigation ("go to test") resolves to the correct source line.

### Layer 2 — the discoverer

```csharp
public sealed class AspireFactDiscoverer : IXunitTestCaseDiscoverer
{
    public ValueTask<IReadOnlyCollection<IXunitTestCase>> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetails(
            discoveryOptions, testMethod, factAttribute);
        var testCase = new AspireXunitTestCase(
            details.ResolvedTestMethod,
            details.TestCaseDisplayName,
            details.UniqueID,
            details.Explicit,
            details.SkipExceptions,
            details.SkipReason,
            details.SkipType,
            details.SkipUnless,
            details.SkipWhen,
            testMethod.Traits.ToReadWrite(StringComparer.OrdinalIgnoreCase),
            timeout: details.Timeout);
        return new([testCase]);
    }
}
```

`TestIntrospectionHelper.GetTestCaseDetails` is xUnit's internal helper that reads all the attribute properties (`Skip`, `Explicit`, `SkipUnless`, `Timeout`, traits…) into a single details record. We delegate to it rather than reading each property manually. The only difference from a normal fact discoverer is the type of test case we return: `AspireXunitTestCase` instead of `XunitTestCase`.

`AspireTheoryDiscoverer` inherits from `TheoryDiscoverer` (the xUnit built-in) and overrides two methods:
- `CreateTestCasesForDataRow` — called when theory data is known at discovery time → returns `AspireXunitTestCase`
- `CreateTestCasesForTheory` — called when data cannot be enumerated until runtime → returns `AspireXunitDelayEnumeratedTestCase` (or a plain `XunitTestCase` for unconditionally-skipped theories)

### Layer 3 — the test case

```csharp
public sealed class AspireXunitTestCase : XunitTestCase, ISelfExecutingXunitTestCase
{
    // Parameterless constructor required by xUnit's serializer.
    // xUnit v3 serializes discovered test cases to disk for caching and
    // reconstructs them via this constructor + IXunitSerializable.Deserialize().
    // Do not remove.
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; must not be removed.")]
    public AspireXunitTestCase() { }

    // ... full constructor ...

    public ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource) =>
            AspireTestCaseRunner.Instance.Run(
                this, messageBus, aggregator.Clone(),
                cancellationTokenSource, TestCaseDisplayName,
                SkipReason, explicitOption, constructorArguments);
}
```

`Run` is the `ISelfExecutingXunitTestCase` contract. It simply hands off to `AspireTestCaseRunner.Instance` — a singleton so we avoid repeated allocations.

### Layer 4 — the test case runner

`AspireTestCaseRunner` extends `XunitTestCaseRunnerBase<TContext, TCase, TTest>`. The base class handles all the xUnit machinery: sending start/finish messages to the bus, managing the cancellation token, aggregating exceptions, and iterating over individual `IXunitTest` instances within the case.

We override only `RunTest`:

```csharp
protected override async ValueTask<RunSummary> RunTest(
    AspireTestCaseRunnerContext ctxt,
    IXunitTest test)
{
    // Open a per-test diagnostics scope. The scope is scoped to this
    // async call chain via AsyncLocal — other concurrently-running tests
    // get their own independent scope.
    using var diagnosticsScope = AspireDiagnosticsContext.BeginScope();

    // Store the scope where AspireXunitTestRunner.OnTestFailed can find it.
    TestContext.Current.KeyValueStorage[AspireXunitTestRunner.ScopeKey] = diagnosticsScope;

    try
    {
        return await AspireXunitTestRunner.Instance.Run(
            test,
            ctxt.MessageBus,
            ctxt.ConstructorArguments,
            ctxt.ExplicitOption,
            ctxt.Aggregator.Clone(),
            ctxt.CancellationTokenSource,
            ctxt.BeforeAfterTestAttributes);
    }
    finally
    {
        TestContext.Current.KeyValueStorage.TryRemove(AspireXunitTestRunner.ScopeKey, out _);
    }
}
```

### Layer 5 — the test runner and `OnTestFailed`

`AspireXunitTestRunner` extends `XunitTestRunner`. xUnit v3's `XunitTestRunner` provides a set of `On*` virtual methods that are called at precise points in the test lifecycle. We override `OnTestFailed`:

```csharp
protected override async ValueTask<(bool Continue, TestResultState ResultState)> OnTestFailed(
    XunitTestRunnerContext ctxt,
    Exception exception,
    decimal executionTime,
    string output,
    string[]? warnings)
{
    var enrichedOutput = output;

    if (TestContext.Current.KeyValueStorage.TryGetValue(ScopeKey, out var scopeObj)
        && scopeObj is BrowserDiagnosticsContextScope diagnosticsScope
        && ctxt.ConstructorArguments
               .OfType<IAspireTestDiagnosticsProvider>()
               .FirstOrDefault() is { } provider)
    {
        var sb = new StringBuilder(output);
        await provider.WriteDiagnosticsAsync(
            ctxt.Test.TestDisplayName,
            diagnosticsScope.BrowserScope,
            line => sb.AppendLine(line));
        enrichedOutput = sb.ToString();
    }

    return await base.OnTestFailed(ctxt, exception, executionTime, enrichedOutput, warnings);
}
```

Key design decisions:
- **`output` is the accumulated `ITestOutputHelper` content** from the test body. We *append* the diagnostics to it — we never replace it — so test-authored output is preserved first.
- **`ctxt.ConstructorArguments`** is the array of resolved fixtures and services that xUnit passed to the test class constructor. `OfType<IAspireTestDiagnosticsProvider>()` finds the fixture without needing to know its position or concrete type.
- **`base.OnTestFailed`** is always called with the enriched output, so xUnit's standard failure handling (message bus, summary counts) proceeds normally.

---

## Browser event capture — `AsyncLocal<T>` scoping

The challenge is that browser events fire on Playwright's event dispatcher threads and need to be captured into the correct test's scope — even when multiple tests run concurrently.

`AsyncLocal<T>` solves this. A value stored in an `AsyncLocal<T>` is visible to the current async call chain and all continuations spawned from it, but is invisible to other async call chains.

```csharp
internal static class AspireDiagnosticsContext
{
    private static readonly AsyncLocal<BrowserDiagnosticsScope?> Scope = new();

    public static BrowserDiagnosticsScope? Current => Scope.Value;

    public static BrowserDiagnosticsContextScope BeginScope()
    {
        var scope = new BrowserDiagnosticsScope();
        Scope.Value = scope;
        return new BrowserDiagnosticsContextScope(scope);  // resets on Dispose
    }
}
```

When `AspireTestCaseRunner.RunTest` calls `BeginScope()`, the `AsyncLocal` value is set on the current logical execution context. Everything that runs as a continuation of that context — the entire test body, including `await page.GotoAsync(...)` and the Playwright event callbacks that fire during it — sees the same scope.

`AspirePageDiagnostics.Track` retrieves the current scope:

```csharp
public static T Track<T>(T page) where T : IPage
{
    AspireDiagnosticsContext.Current?.Attach(page);
    return page;
}
```

`BrowserDiagnosticsScope.Attach` wires Playwright event handlers:

```csharp
internal void Attach(IPage page)
{
    page.Console  += (_, msg) => Add($"[browser:{msg.Type}] {msg.Text}");
    page.PageError += (_, msg) => Add($"[browser:pageerror] {msg}");
}
```

These handlers write timestamped entries into the scope's `RingBuffer`. They are called by Playwright's event system — no polling, no explicit collection code in tests.

---

## Aspire resource observation

### Resource state

`AspireFixtureBase.BeginWatching` starts a background loop that consumes `DistributedApplication.ResourceNotifications`:

```csharp
await foreach (var resourceEvent in app.ResourceNotifications.WatchAsync(cancellationToken))
{
    _resourceStates[resourceEvent.Resource.Name] = resourceEvent;
    EnsureResourceWatcher(resourceEvent.Resource.Name);
}
```

`ResourceNotifications.WatchAsync` is an Aspire API that emits a `ResourceEvent` every time any resource transitions state (starting → running → exiting, etc.). `_resourceStates` stores the **latest** snapshot per resource name. `EnsureResourceWatcher` also starts a log watcher for any newly-discovered resource so dynamically-created sidecars and containers are covered automatically.

### Resource logs

Each resource gets its own background log-collection task:

```csharp
private async Task WatchResourceLogsAsync(string resourceName, CancellationToken ct)
{
    // Replay logs already emitted before this watcher started
    await foreach (var batch in _resourceLoggerService.GetAllAsync(resourceName, ct))
        AppendLogBatch(resourceName, batch);

    // Then stream live logs going forward
    await foreach (var batch in _resourceLoggerService.WatchAsync(resourceName, ct))
        AppendLogBatch(resourceName, batch);
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
```

`ResourceLoggerService` is an Aspire-internal service that aggregates stdout/stderr from all managed processes. Each resource's `RingBuffer<string>` is capped at 80 lines — the oldest is evicted when the buffer is full, so only the most recent output is kept. This prevents memory growth during long test runs.

---

## `BufferingLoggerProvider` and `AppHost` logs

Aspire's hosting infrastructure logs a lot during startup (DCP orchestration, resource health probes, dashboard boot). Without intervention, all of this appears on the console for every test run — signal-to-noise is terrible.

`BufferingLoggerProvider` intercepts the AppHost's `ILoggerFactory` and routes log output into a capped `RingBuffer<string>`. The fixture exposes it as `BufferingLogging`:

```csharp
protected Action<ILoggingBuilder> BufferingLogging =>
    logging =>
    {
        logging.ClearProviders();
        logging.AddProvider(_appHostLogProvider);
    };
```

Two categories are silently dropped via `BufferingLogExcludedPrefixes` rather than buffered:

- **`System.Net.Http.HttpClient.*`** — Aspire's internal health-check HTTP client pings (excluded by default in the library). Very frequent, low-signal, and also visible in OTEL spans.
- **App-specific resource stdout** — e.g. `MyApp.AppHost.Resources.*`, piped through Aspire's orchestration. These are captured verbatim via OTEL structured logs; buffering them again would duplicate output. Consumers add their own prefix:

```csharp
BufferingLogExcludedPrefixes.Add("MyApp.AppHost.Resources.");
builder.Services.AddLogging(BufferingLogging);
```

`BufferingLogging` calls `logging.AddFilter(prefix, LogLevel.None)` for each entry. Because `ClearProviders()` is called first and only `_appHostLogProvider` is added, the filter rules only affect the buffer — no log levels are changed globally.

---

## `RingBuffer<T>` — bounded memory

All diagnostic buffers in the library use `RingBuffer<T>`. The design is simple:

```csharp
public sealed class RingBuffer<T>(int capacity)
{
    private readonly Queue<T> _items = new();
    private readonly Lock     _lock  = new();

    public void Add(T item)
    {
        lock (_lock)
        {
            if (_items.Count >= capacity) _items.Dequeue();  // evict oldest
            _items.Enqueue(item);
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_lock) { return _items.ToArray(); }
    }
}
```

Capacities:
| Buffer | Default cap | Rationale |
|---|---|---|
| `_resourceLogs[name]` | 80 lines | Enough context for most failures; avoids megabytes of build-system output |
| `BrowserDiagnosticsScope._entries` | 120 events | Browser consoles are noisier than server logs |
| `BufferingLoggerProvider` | 500 lines | AppHost startup is chatty; cap set high to avoid losing early-boot context |

---

## Data flow — complete picture

```
AppHostFixture.InitializeAsync()
│
├── DistributedApplicationTestingBuilder.CreateAsync<T>()  ← builds the Aspire AppHost
├── builder.Services.AddLogging(BufferingLogging)          ← redirects log output to buffer
├── _app.StartAsync()                                       ← Aspire stack is live
│
├── BeginWatching(_app, "api", "frontend")
│   ├── WatchResourceNotificationsAsync()  ← background loop
│   │       _resourceStates[name] = latestEvent
│   └── WatchResourceLogsAsync(name)       ← one background loop per resource
│           _resourceLogs[name].Add(line)  (capped ring buffer)
│
Per test:
│
├── AspireXunitTestCase.Run()
│   └── AspireTestCaseRunner.RunTest()
│       ├── AspireDiagnosticsContext.BeginScope()    ← AsyncLocal scope set
│       ├── TestContext.Current.KeyValueStorage[ScopeKey] = diagnosticsScope
│       │
│       └── AspireXunitTestRunner.Instance.Run()    ← test method executes
│               AspirePageDiagnostics.Track(page)
│                   page.Console / page.PageError → BrowserDiagnosticsScope.Add()
│
│           if test fails → OnTestFailed():
│               ├── KeyValueStorage[ScopeKey]         → BrowserDiagnosticsScope
│               ├── ConstructorArguments              → IAspireTestDiagnosticsProvider (fixture)
│               └── fixture.WriteDiagnosticsAsync()
│                   ├── _resourceStates               → "Resource states:" block
│                   ├── _resourceLogs                 → "Recent resource output [X]:" blocks
│                   ├── browserScope.Entries          → "Recent browser console:" block
│           ├── _appHostLogProvider.Snapshot() → "AppHost log output:" block
│           └── _traceCapture.CaptureFailureDiagnosticsAsync() → "OTEL telemetry" block
│                   fetches /api/telemetry/spans and /api/telemetry/logs from dashboard
│                   writes spans.otlp.json + logs.otlp.json to %TEMP%\aspire-test-otel\
│                   (same OtelTraceCapture instance tests use for in-test trace assertions)
│
AppHostFixture.DisposeAsync()
├── base.DisposeAsync()   ← cancels background watchers, awaits their completion
└── _app.DisposeAsync()   ← stops the Aspire stack
```

---

## Files at a glance

| File | Role |
|---|---|
| `AspireXunitAttributes.cs` | `[AspireFact]`, `[AspireTheory]`, discoverers, test-case classes, runner, `OnTestFailed` hook |
| `AspireFixtureBase.cs` | Abstract base fixture — resource-state watching, log buffering, `WriteDiagnosticsAsync`, `EnableTraceCapture` |
| `AspireDiagnostics.cs` | `AspirePageDiagnostics`, `AspireDiagnosticsContext`, `BrowserDiagnosticsScope`, `BufferingLoggerProvider`, `RingBuffer<T>` |
| `IAspireTestDiagnosticsProvider.cs` | Interface between the runner and the fixture |
| `OtelTraceCapture.cs` | Dashboard-backed trace verification (`Reset`/`WaitForAsync`/assertions) and failure-diagnostics span/log capture — one HTTP client, two use cases |
| `OtlpSpanParser.cs` | `CapturedSpan` record and the shared OTLP `resourceSpans → scopeSpans → spans` JSON walk used by `OtelTraceCapture` |

