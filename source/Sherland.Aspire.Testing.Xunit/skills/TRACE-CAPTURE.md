---
name: sherland-aspire-trace-capture
description: Verify OTEL trace spans — root-span counts, parent/child shape, and error status — for both direct API calls and browser interactions, plus automatic failure diagnostics.
packages: Sherland.Aspire.Testing.Xunit
---

# Verify Traces With OtelTraceCapture

`OtelTraceCapture` verifies trace shape regardless of whether the trace came from a direct
`HttpClient` call in the test or a Playwright interaction. Backend-originated spans are
observed by polling the Aspire dashboard's REST telemetry API. Browser-originated spans are
additionally observed by intercepting the page's outgoing OTLP export directly
(`AttachBrowserCaptureAsync`) rather than relying solely on that export reaching the
dashboard — the dashboard's OTLP/HTTP endpoint is dynamically allocated by the test host and
isn't always reachable or CORS-permitted from a test-hosted browser page. Both sources feed
the same `Spans`/`RootSpans` collections. The same capture instance is also used
automatically for post-failure diagnostics.

## Setup

1. Before `BuildAsync()`, keep the dashboard enabled and make its API accessible without an
   API key:
   - `builder.AppHostOptions.DisableDashboard = false;`
   - Set `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` as an **environment variable**
     (`Environment.SetEnvironmentVariable(...)`) before creating the builder — the dashboard
     runs as its own child process and reads configuration from its own OS environment, not
     from the AppHost's in-memory `IConfiguration`; setting
     `builder.Configuration["Dashboard:..."]` does not reliably reach it.
2. After `StartAsync()`:
   - `BeginWatching(_app, ...)`
   - `Traces = EnableTraceCapture(_app) ?? throw new InvalidOperationException(...);`
3. Optionally tune flush timing: `TelemetryFlushDelay = TimeSpan.FromSeconds(2);` (affects
   only the automatic failure-diagnostics query, not `WaitForAsync`).
4. For snappier exports (both in demos and to keep trace-assertion tests from racing the
   OTLP batch exporter's default ~5s scheduled delay), set the standard
   `OTEL_BSP_SCHEDULE_DELAY` env var (milliseconds) on backend project resources in the
   AppHost, e.g. `api.WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "200")`.
5. If you set the env vars from step 1 in the fixture itself, clear them afterward (e.g. in
   a `finally` around `CreateAsync`/`BuildAsync`/`StartAsync`) so they don't leak into
   unrelated processes started later in the same test-host session. The sample instead sets
   them unconditionally at the top of the AppHost's own `Program.cs` (see below) — one place
   that covers both normal `aspire run` usage and tests, with no cleanup needed since it's
   set once per AppHost process rather than per fixture.

### Making browser traces visible in the dashboard outside of tests too

The dashboard's OTLP/HTTP endpoint isn't configured by default when launched via the
AppHost (only OTLP/gRPC is), and its frontend endpoint does **not** itself accept OTLP
traffic despite matching the `/v1/traces` route (it responds "Connection types 'OtlpGrpc,
OtlpHttp' are not enabled on this connection"). A test-hosted
`DistributedApplicationTestingBuilder` run additionally doesn't reliably bind the exact
OTLP/HTTP port requested, which is why `AttachBrowserCaptureAsync` (below) exists — it
intercepts the browser's export directly instead of depending on it reaching a real
destination.

If you also want a developer to see browser traces in the dashboard UI itself when just
running the app normally (`aspire run`/`aspire start`, not through tests), wire a
dedicated OTLP/HTTP endpoint into the AppHost's `Program.cs` unconditionally — a dynamic
free port, `ASPIRE_ALLOW_UNSECURED_TRANSPORT` + `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`,
and permissive `Dashboard:Otlp:Cors`. This has no bearing on the test-side workflow above
(tests keep working via `AttachBrowserCaptureAsync` either way) — it only matters if you
want the real dashboard to show frontend spans too. See
`samples/Sherland.Aspire.AppHost/Program.cs` for a complete, verified example; verify with
`aspire otel traces`/`aspire otel spans` after interacting with the app in a browser.

## Assertion Workflow (same for API calls and browser clicks)

1. If a browser action is involved, attach interception first:
   `await using var browserCapture = await fixture.Traces.AttachBrowserCaptureAsync(page);`
2. `fixture.Traces.Reset()` immediately before the action under test.
3. Perform the action — an `HttpClient` call, a Playwright click, or both.
4. If a browser action was involved, optionally call `TriggerBrowserFlushAsync(page)` to
   shave polling latency (not required for correctness).
5. `await fixture.Traces.WaitForAsync(spans => /* condition */);` — dashboard ingestion is
   asynchronous, so always wait for the expected condition rather than asserting immediately.
6. Assert: `AssertRootSpanCount(n)` / `AssertSingleRootSpan(predicate)`,
   `AssertAllDirectChildren(...)`, `AssertNoOrphanedSpans()`.

## Example: direct API call

```csharp
fixture.Traces.Reset();

using var client = fixture.App.CreateHttpClient("api");
await client.PostAsync("/items/3/process", content: null);

await fixture.Traces.WaitForAsync(spans => spans.Any(s => s.IsRoot));
fixture.Traces.AssertRootSpanCount(1);
fixture.Traces.AssertNoOrphanedSpans();
```

## Example: browser click

```csharp
await using var browserCapture = await fixture.Traces.AttachBrowserCaptureAsync(page);
fixture.Traces.Reset();

await page.GetByTestId("extended-mode-toggle").ClickAsync();
await fixture.Traces.TriggerBrowserFlushAsync(page);

await fixture.Traces.WaitForAsync(spans => spans.Any(s => s.IsRoot && s.Name == "mode:extended"));
fixture.Traces.AssertSingleRootSpan(s => s.Name == "mode:extended");
```

## Example: one API call + one browser click = two root spans

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

## Example: error spans

```csharp
fixture.Traces.Reset();

using var client = fixture.App.CreateHttpClient("api");
await client.PostAsync("/items/5/fail", content: null);

await fixture.Traces.WaitForAsync(spans => spans.Any(s => s.Name == "items:fail"));
fixture.Traces.AssertHasErrorSpan(s => s.Name == "items:fail");
```

## Automatic Failure Diagnostics

No extra code is needed beyond setup. When a test fails, `EnableTraceCapture`'s instance is
queried again automatically: span/log counts and OTLP JSON artifact paths
(`spans.otlp.json`, `logs.otlp.json`) are appended to the failure output. This never affects
pass/fail — it is best-effort and swallows its own errors.

## Failure Interpretation

- Multiple matching root spans: interaction scope is started more than once for the same action.
- Child spans not attached to root: async work escaped the intended parent region.
- Orphaned spans: trace structure is broken (missing parent span ID in captured set).
- `WaitForAsync` timeout: the expected span never arrived — check the dashboard API is
  enabled and reachable, or increase the timeout for slow environments.
