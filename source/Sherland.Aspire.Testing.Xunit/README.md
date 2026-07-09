# Sherland.Aspire.Testing.Xunit

**Silent on success. Complete diagnostics on failure.**

`Sherland.Aspire.Testing.Xunit` is an xUnit v3 integration library for [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/aspire-overview) distributed-application tests. It provides `[AspireFact]` and `[AspireTheory]` — drop-in replacements for `[Fact]` and `[Theory]` that activate automatic failure diagnostics.

It also includes `OtelTraceCapture`, a single trace-verification API that works the same whether a trace was produced by a direct backend API call or a Playwright browser interaction — asserting root-span counts, parent/child shape, and error status against the Aspire dashboard's own telemetry.

When a test **passes**, output is completely silent.  
When a test **fails**, the test console prints:

- **Resource state** — each Aspire resource's current state, health status, and exit code
- **Resource logs** — the most recent log lines from every managed process (API, workers, frontend…)
- **AppHost logs** — buffered Aspire infrastructure output (startup, orchestration, health checks)
- **Browser console** — console messages and JavaScript errors captured via Playwright *(opt-in per page)*

No `Console.WriteLine` in test code. No manual log wiring. No grepping CI output.

---

## Contents

- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Tracking browser console output (Playwright)](#tracking-browser-console-output-playwright)
- [Verifying traces — API calls and browser interactions, one API](#verifying-traces--api-calls-and-browser-interactions-one-api)
- [What failure output looks like](#what-failure-output-looks-like)
- [Controlling which tests run](#controlling-which-tests-run)
- [AI skills in this package](#ai-skills-in-this-package)
- [API reference](#api-reference)
- [Implementation internals](#implementation-internals)

---

## Requirements

| Dependency | Minimum version |
|---|---|
| .NET | 10.0 |
| xUnit | v3 — `xunit.v3` ≥ 3.2.2 |
| .NET Aspire Hosting Testing | 13.x |
| Microsoft.Playwright | 1.59 *(only for browser diagnostics)* |

> **xUnit v3 requirement:** All xUnit v3 test projects must set `<OutputType>Exe</OutputType>` in their `.csproj`. xUnit v3 test assemblies run as standalone executables.

---

## Getting started

### Step 1 — Add the package to your test project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageReference Include="Sherland.Aspire.Testing.Xunit" Version="1.0.0" />
  </ItemGroup>
</Project>
```

### Step 2 — Create a fixture

The fixture starts and owns the `DistributedApplication`. Extend `AspireFixtureBase` — it wires up the background log and resource-state watchers so you don't have to.

```csharp
using Aspire.Hosting.Testing;
using Sherland.Aspire.Testing.Xunit;
using Microsoft.Extensions.DependencyInjection;

public sealed class AppFixture : AspireFixtureBase
{
    private DistributedApplication? _app;

    // Expose the running app so test classes can create HTTP clients, resolve
    // services, and read endpoints.
    internal DistributedApplication App => _app!;

    public override async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.MyAppHost>(args: []);

        // Route AppHost log output into the failure-diagnostics buffer.
        // Without this, Aspire's startup noise appears on every test run.
        // With it, the logs only appear when a test fails — right where you need them.
        //
        // Aspire health-check HttpClient noise is excluded by default.
        // Add your own app-specific prefixes for categories that live in OTEL already:
        BufferingLogExcludedPrefixes.Add("MyApp.AppHost.Resources.");
        builder.Services.AddLogging(BufferingLogging);

        _app = await builder.BuildAsync();
        await _app.StartAsync();

        // Start watching these resources immediately. Any additional resources
        // that appear at runtime (sidecars, dynamic containers) are discovered
        // and added to the watchers automatically.
        BeginWatching(_app, "api", "frontend");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();          // stops background watchers — always call first
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
```

### Step 3 — Share the fixture across test classes

xUnit **collections** let multiple test classes share one fixture instance, so the Aspire stack starts once and stays up for the entire test run.

```csharp
// Define the collection once — this class has no body.
[CollectionDefinition("AppTests")]
public class AppTestsCollection : ICollectionFixture<AppFixture> { }
```

### Step 4 — Write tests

Use `[AspireFact]` and `[AspireTheory]` exactly as you would use `[Fact]` and `[Theory]`. Receive the fixture via the primary constructor.

```csharp
[Collection("AppTests")]
public class ApiTests(AppFixture fixture)
{
    [AspireFact]
    public async Task GetHealth_ReturnsOk()
    {
        var client = fixture.App.CreateHttpClient("api");
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    [AspireTheory]
    [InlineData("/health")]
    [InlineData("/ready")]
    public async Task HealthEndpoints_ReturnOk(string path)
    {
        var client = fixture.App.CreateHttpClient("api");
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
    }
}
```

That is everything needed. If `GetHealth_ReturnsOk` fails, the test output automatically includes the current state of every watched Aspire resource and the last N log lines from each managed process.

---

## Tracking browser console output (Playwright)

Wrap every new Playwright page in `AspirePageDiagnostics.Track` to capture browser console messages and JavaScript errors:

```csharp
[Collection("AppTests")]
public class BrowserTests(AppFixture fixture) : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser?    _browser;
    private IBrowserContext? _context;

    // xUnit v3: IAsyncLifetime uses ValueTask, not Task.
    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser    = await _playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        _context    = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        if (_browser  is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    [AspireFact]
    public async Task SearchPage_Loads()
    {
        // One call — browser console and page errors are now captured automatically.
        var page = AspirePageDiagnostics.Track(await _context!.NewPageAsync());

        var baseUri = fixture.App.GetEndpoint("frontend");
        await page.GotoAsync(baseUri.ToString());
        await page.WaitForSelectorAsync("[data-testid='search-input']");
    }
}
```

`Track` is a pass-through — it returns the page unchanged so it can be inlined on any expression.

---

## Verifying traces — API calls and browser interactions, one API

`OtelTraceCapture` verifies root-span counts and shape regardless of whether a trace came from a direct `HttpClient` call in the test or a Playwright click. Backend-originated spans are observed by polling the Aspire dashboard's REST telemetry API. Browser-originated spans are additionally observed by intercepting the page's outgoing OTLP export directly (`AttachBrowserCaptureAsync`) — this doesn't depend on that export reaching a reachable dashboard endpoint, since the dashboard's OTLP/HTTP port is dynamically allocated by the test host and isn't always reachable or CORS-permitted from a test-hosted browser page. Both sources feed the same `Spans`/`RootSpans` collections.

For the full workflow (setup, assertions, error spans, and failure diagnostics), see the package skill:

- `skills/TRACE-CAPTURE.md`

### Setup required for `EnableTraceCapture` to work

`EnableTraceCapture` talks to the Aspire dashboard's REST telemetry API, which requires an API key by default. The dashboard runs as its **own child process** — it reads configuration from its own OS environment, not from the AppHost's in-memory `IConfiguration`, so `builder.Configuration["Dashboard:..."] = ...` does **not** reliably reach it. Set the following as real environment variables, before creating the builder:

```csharp
// Before DistributedApplicationTestingBuilder.CreateAsync(...):
Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS", "true");

var appHost = await DistributedApplicationTestingBuilder
    .CreateAsync<Projects.MyAppHost>(
        args: [],
        configureBuilder: (appOptions, _) => { appOptions.DisableDashboard = false; });

_app = await appHost.BuildAsync();
await _app.StartAsync();

BeginWatching(_app, "api", "frontend");
Traces = EnableTraceCapture(_app)
    ?? throw new InvalidOperationException("Trace capture unavailable — dashboard API not enabled.");
```

`ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` is a shortcut that sets `Dashboard:Frontend:AuthMode`, `Dashboard:Otlp:AuthMode`, and `Dashboard:Api:AuthMode` all to `Unsecured` — appropriate for local test runs (per Aspire's own guidance, not for anything publicly reachable). If you set it in the fixture as shown above (rather than in the AppHost itself — see below), clear it afterward in a `finally` block so it doesn't leak into unrelated processes started later in the same session.

The sample takes a slightly different, arguably better approach: it sets this (and the browser OTLP wiring below) **unconditionally at the top of the AppHost's own `Program.cs`**, not in the test fixture — since `DistributedApplicationTestingBuilder` executes that same top-level code, one place covers both normal `aspire run` usage and tests. See [samples/Sherland.Aspire.AppHost/Program.cs](https://github.com/sherland/Sherland.Aspire.Testing/blob/main/samples/Sherland.Aspire.AppHost/Program.cs) and [samples/Sherland.Aspire.DemoTests/DemoFixture.cs](https://github.com/sherland/Sherland.Aspire.Testing/blob/main/samples/Sherland.Aspire.DemoTests/DemoFixture.cs) for the full working pattern.

Optionally, also lower the OTLP batch exporter's export delay on your backend project resources so spans reach the dashboard promptly instead of waiting on its ~5s default, both for snappier feedback and so `WaitForAsync` isn't racing that interval:

```csharp
// In the AppHost's Program.cs:
var api = builder.AddProject<Projects.MyApi>("api")
    .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "200");
```

**Browser-originated spans need one more step.** The dashboard's OTLP/HTTP endpoint isn't configured by default when launched via the AppHost (only OTLP/gRPC is) — and when it *is* explicitly requested, a test-hosted `DistributedApplicationTestingBuilder` run doesn't reliably bind the exact port requested, so there's no way to hand the browser a working endpoint up front. That's exactly why `AttachBrowserCaptureAsync` exists (below): it intercepts the browser's outgoing export directly, so it works regardless of whether that export ever reaches a real destination. **If you also want a developer to see browser traces in the dashboard UI itself** (outside of tests, running the app normally), wire a dedicated OTLP/HTTP endpoint into the AppHost unconditionally — see [samples/Sherland.Aspire.AppHost/Program.cs](https://github.com/sherland/Sherland.Aspire.Testing/blob/main/samples/Sherland.Aspire.AppHost/Program.cs) for a complete, verified example (dynamic free port, `ASPIRE_ALLOW_UNSECURED_TRANSPORT`, and `Dashboard:Otlp:Cors`).

Asserting a direct API call produces exactly one root span:

```csharp
fixture.Traces.Reset();

using var client = fixture.App.CreateHttpClient("api");
await client.PostAsync("/items/3/process", content: null);

await fixture.Traces.WaitForAsync(spans => spans.Any(s => s.IsRoot));
fixture.Traces.AssertRootSpanCount(1);
```

Asserting a browser click produces exactly one root span — same API, same assertions. `AttachBrowserCaptureAsync` must be attached to the page before the action under test, since it works by intercepting the page's outgoing OTLP export:

```csharp
await using var browserCapture = await fixture.Traces.AttachBrowserCaptureAsync(page);
fixture.Traces.Reset();

await page.GetByTestId("extended-mode-toggle").ClickAsync();
await fixture.Traces.TriggerBrowserFlushAsync(page); // optional: shaves polling latency

await fixture.Traces.WaitForAsync(spans => spans.Any(s => s.IsRoot && s.Name == "mode:extended"));
fixture.Traces.AssertSingleRootSpan(s => s.Name == "mode:extended");
```

And combining both in a single window — one API call plus one browser click should add up to exactly two root spans:

```csharp
await using var browserCapture = await fixture.Traces.AttachBrowserCaptureAsync(page);
fixture.Traces.Reset();

using var client = fixture.App.CreateHttpClient("api");
await client.PostAsync("/items/4/process", content: null);

await page.GetByTestId("process-item-2").ClickAsync();
await fixture.Traces.TriggerBrowserFlushAsync(page);

await fixture.Traces.WaitForAsync(spans => spans.Count(s => s.IsRoot) >= 2);
fixture.Traces.AssertRootSpanCount(2);
```

Verifying error spans works the same way:

```csharp
fixture.Traces.Reset();

using var client = fixture.App.CreateHttpClient("api");
await client.PostAsync("/items/5/fail", content: null);

await fixture.Traces.WaitForAsync(spans => spans.Any(s => s.Name == "items:fail"));
fixture.Traces.AssertHasErrorSpan(s => s.Name == "items:fail");
```

This same `OtelTraceCapture` instance is also used automatically for failure diagnostics — see *What failure output looks like* below.

---

## What failure output looks like

```
========== ASPIRE DIAGNOSTICS: SearchPage_Loads ==========
Resource states:
  - api: state=Running, health=Healthy
  - frontend: state=Exiting, health=<unknown>, exit_code=1
  - aspire-dashboard: state=Running, health=<unknown>

Recent resource output [api]:
     1 [OUT] info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080
   ...

Recent resource output [frontend]:
    79 [ERR] Error: Cannot find module './components/SearchInput'
    80 [ERR] npm error Exit status 1

Recent browser console / trace output:
  14:23:01.142 [browser:error] Failed to load resource: net::ERR_CONNECTION_REFUSED
  14:23:01.200 [browser:pageerror] TypeError: Cannot read properties of undefined

AppHost log output:
  14:22:59.003 [info] Aspire.Hosting: Application started.
  14:23:00.011 [warn] Aspire.Hosting.Dashboard: Resource 'frontend' exited early.

OTEL telemetry (this test / total stored in dashboard):
  Spans : 4 in test window / 27 total
  Logs  : 12 in test window / 94 total
  Artefacts:
    C:\Users\you\AppData\Local\Temp\aspire-test-otel\SearchPage_Loads\spans.otlp.json
    C:\Users\you\AppData\Local\Temp\aspire-test-otel\SearchPage_Loads\logs.otlp.json
======== END ASPIRE DIAGNOSTICS ========
```

The diagnostics block is routed through xUnit's message bus and appears correctly associated with the failing test in every output sink — `dotnet test` terminal, Visual Studio Test Explorer, JetBrains Rider, GitHub Actions test reports, and Azure DevOps.

---

## Controlling which tests run

For convention and execution-pattern guidance (including environment gating and explicit tests), see:

- `skills/TEST-CONVENTIONS.md`

---

## Enforcing the convention

See `skills/TEST-CONVENTIONS.md` for the full convention-enforcement pattern and example guard test.

---

## AI skills in this package

This NuGet package ships an embedded skill file at `skills/SKILL.md` so AI coding tools can load package-specific usage guidance.

### Option A - `nuget-skills` (quickest)

Prerequisites:

- .NET SDK installed
- Package already referenced by your test project

```bash
# Install tool once
dotnet tool install --global nuget-skills

# Install agent integration (example: Copilot)
nuget-skills install --agent all

# Discover skills from packages in your solution/project
nuget-skills scan --project path/to/your.slnx

# Load this package skill
nuget-skills load Sherland.Aspire.Testing.Xunit
```

Expected scan output should include this package with a local source, for example:

```text
Sherland.Aspire.Testing.Xunit  1.0.0  [local]
```

### Option B - `agentskills-cli`

`agentskills-cli` supports NuGet-backed skill sources in the open Agent Skills ecosystem.

```bash
# Install tool once
dotnet tool install --global agentskills-cli

# Inspect skills available from your configured sources
agentskills-cli find Sherland.Aspire.Testing.Xunit

# Or add from a NuGet package source when configuring skills in your environment
agentskills-cli add Sherland.Aspire.Testing.Xunit
```

If your environment uses a different source setup or private feeds, configure the tool to match your NuGet feed settings, then repeat `find`/`add`.

### What the skill teaches

- Correct `AspireFixtureBase` lifecycle usage
- When and how to use `[AspireFact]` and `[AspireTheory]`
- Playwright diagnostics integration with `AspirePageDiagnostics.Track`
- Unified trace verification (root-span counts, shape, error spans) with `OtelTraceCapture` and `EnableTraceCapture`

Maintainer details for how skills are versioned and validated live in `docs/sherland-aspire-testing-xunit-skills.md`.

---

## API reference

### Attributes

| Attribute | Description |
|---|---|
| `[AspireFact]` | Drop-in replacement for `[Fact]`. Activates the diagnostics pipeline for this test. |
| `[AspireTheory]` | Drop-in replacement for `[Theory]`. Same as `[AspireFact]` for parameterised tests. |

Both support all standard properties: `DisplayName`, `Skip`, `Explicit`, `Timeout`, `SkipUnless`, `SkipWhen`, `SkipType`.

### `AspireFixtureBase`

Abstract base class for xUnit fixtures that start a `DistributedApplication`.

| Member | Description |
|---|---|
| `BeginWatching(app, params string[] resources)` | Starts background resource-state and log watching. Call after `_app.StartAsync()`. |
| `EnableTraceCapture(app)` | Resolves the dashboard URL and returns an `OtelTraceCapture` for in-test trace assertions; the same instance is also used automatically for failure diagnostics. Call after `BeginWatching`. Requires the dashboard enabled (`DisableDashboard = false`) and `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` set as an environment variable before building — see *Setup required for `EnableTraceCapture` to work* above. |
| `TelemetryFlushDelay` | `TimeSpan` (default: 1 s). Delay before querying the dashboard on failure, to let in-flight telemetry arrive. |
| `BufferingLogging` | `Action<ILoggingBuilder>` — pass to `builder.Services.AddLogging(...)` to silence passing-test output. |
| `InitializeAsync()` | **Abstract.** Override to build and start the Aspire app, then call `BeginWatching`. |
| `DisposeAsync()` | Virtual. Stops background watchers. Always `await base.DisposeAsync()` before disposing the app. |
| `WriteDiagnosticsAsync(testName, browserScope, writeLine)` | Writes the diagnostics block. Called automatically on failure; override to add custom context. |

### `AspirePageDiagnostics`

| Member | Description |
|---|---|
| `Track<T>(page)` | Attaches browser console and page-error capture to `page`. Returns `page` unchanged. |

### `OtelTraceCapture`

Returned by `EnableTraceCapture(app)`. Polls the Aspire dashboard's REST telemetry API for backend-originated spans and, when `AttachBrowserCaptureAsync` is used, also merges browser-originated spans captured via Playwright network interception — so assertions apply uniformly regardless of trace origin.

| Member | Description |
|---|---|
| `Reset()` | Marks the start of a new assertion window, clearing both the dashboard snapshot and any browser-captured spans. Call immediately before the action under test. |
| `Spans` / `RootSpans` | The merged dashboard + browser-captured spans, filtered to the current window and deduplicated by span ID. |
| `RefreshAsync(limit, ct)` | Polls the dashboard once and updates the snapshot. |
| `WaitForAsync(condition, timeout, pollInterval, ct)` | Polls until `condition` is satisfied or the timeout elapses (throws `TimeoutException`). Always precede assertions with this rather than a single `RefreshAsync`. |
| `AttachBrowserCaptureAsync(page, otlpRoutePattern)` | Intercepts the page's outgoing OTLP export and merges captured spans into `Spans`/`RootSpans`. Attach before the action under test; dispose (or `await using`) at the end of the test to stop intercepting. |
| `TriggerBrowserFlushAsync(page, timeout)` | Optional: forces the browser's batch span processor to export early, reducing polling latency. Verification never depends on this being called. |
| `AssertRootSpanCount(expected, predicate?)` / `AssertSingleRootSpan(predicate)` | Assert the number of root spans (optionally matching a predicate). |
| `AssertAllDirectChildren(parentPredicate, childPredicate)` | Assert every matching child span's parent is the single matching root span. |
| `AssertNoOrphanedSpans(exclude?)` | Assert no span references a parent span ID outside the captured set. |
| `AssertHasErrorSpan(predicate?)` / `AssertNoErrorSpans(exclude?)` | Assert the presence or absence of spans with OTLP error status. |

### `IAspireTestDiagnosticsProvider`

Interface that `AspireFixtureBase` already implements. Implement it directly only if you need a fixture that cannot extend `AspireFixtureBase`.

```csharp
public interface IAspireTestDiagnosticsProvider
{
    ValueTask WriteDiagnosticsAsync(
        string testName,
        BrowserDiagnosticsScope? browserScope,
        Action<string> writeLine);
}
```

The `writeLine` delegate routes output through xUnit's message bus — do not write to `Console` directly or the output will not be associated with the failing test.

### `BrowserDiagnosticsScope`

Holds browser events captured during one test. Normally managed automatically by `[AspireFact]`/`[AspireTheory]` and populated by `AspirePageDiagnostics.Track`. Access the captured entries via `Entries` if you need to inspect them in a custom `WriteDiagnosticsAsync` override.

### `BufferingLoggerProvider`

An `ILoggerProvider` that writes formatted log lines to an in-memory `RingBuffer<string>` instead of stdout. The `AspireFixtureBase.BufferingLogging` helper registers it on the AppHost logging pipeline.

| Member | Description |
|---|---|
| `BufferingLoggerProvider(int capacity = 500)` | Creates the provider with a capped buffer of `capacity` lines. |
| `Snapshot()` | Returns all current buffer entries as a read-only list. |

### `RingBuffer<T>`

A fixed-capacity thread-safe circular buffer. When the buffer is full, the oldest entry is evicted to make room for the newest. Used throughout the library to cap memory usage during long test runs.

| Member | Description |
|---|---|
| `RingBuffer<T>(int capacity)` | Creates the buffer. |
| `Add(T item)` | Adds an item; evicts the oldest if at capacity. |
| `Snapshot()` | Returns a consistent point-in-time copy of all current entries. |

---

## Implementation internals

For a detailed explanation of the xUnit v3 extensibility points used — custom test-case discoverers, `ISelfExecutingXunitTestCase`, the `XunitTestCaseRunnerBase` + `XunitTestRunner.OnTestFailed` pipeline, `AsyncLocal<T>` scoping for per-test browser event isolation, and the ring-buffer design — see [docs/internals.md](https://github.com/sherland/Sherland.Aspire.Testing/blob/main/source/Sherland.Aspire.Testing.Xunit/docs/internals.md).

> This file is not packed into the NuGet package — the link above always points at GitHub regardless of where this README is being viewed from (nuget.org, GitHub Packages, or a text editor after extracting the `.nupkg`).

