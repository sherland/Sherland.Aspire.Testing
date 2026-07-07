---
name: sherland-aspire-testing-xunit
description: Use Sherland.Aspire.Testing.Xunit for Aspire integration tests with failure-first diagnostics in xUnit v3.
packages: Sherland.Aspire.Testing.Xunit
---

# Sherland.Aspire.Testing.Xunit

## When To Use

Use this package when a .NET Aspire integration test suite uses xUnit v3 and you need rich diagnostics only when tests fail.

## Focused Workflows

- `TRACE-CAPTURE.md` — verify root-span counts, parent/child shape, and error spans for both API calls and browser interactions, plus automatic failure diagnostics.
- `TEST-CONVENTIONS.md` — enforce `[AspireFact]`/`[AspireTheory]` and execution conventions.

## Core Pattern

1. Create a shared fixture that inherits from `AspireFixtureBase`.
2. Start the `DistributedApplication` in `InitializeAsync`.
3. Call `BeginWatching` for the resources you want tracked.
4. Use `[AspireFact]` and `[AspireTheory]` instead of `[Fact]` and `[Theory]`.

## Browser Diagnostics Pattern

For Playwright tests, wrap each page in `AspirePageDiagnostics.Track`:

```csharp
var page = AspirePageDiagnostics.Track(await context.NewPageAsync());
```

This captures browser console and page errors and emits them with test failure diagnostics.

## Trace Capture Pattern

`OtelTraceCapture` verifies trace shape (root-span counts, parent/child, error status) for
both direct API calls and browser interactions, and doubles as the source for automatic
failure diagnostics (OTEL counts and exported JSON on failure):

1. Enable the Aspire dashboard API in the test builder configuration.
2. Call `Traces = EnableTraceCapture(app)` after `BeginWatching`.
3. In tests: `fixture.Traces.Reset()` before the action, `WaitForAsync(...)` after, then assert.

If prerequisites are missing, trace capture is skipped safely and explained in output.

## xUnit v3 Requirement

xUnit v3 test projects must set:

```xml
<OutputType>Exe</OutputType>
```

## Common Pitfalls

- Using `[Fact]` instead of `[AspireFact]` drops package diagnostics.
- Forgetting `AspirePageDiagnostics.Track` hides browser console/page errors.
- Calling `EnableTraceCapture` without dashboard API enabled results in skipped trace capture.
- Asserting immediately after an action without `WaitForAsync` races dashboard ingestion — always wait for the expected condition first.

