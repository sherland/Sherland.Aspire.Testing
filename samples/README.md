# Sherland Aspire Samples

This folder contains self-contained Aspire samples for `Sherland.Aspire.Testing.Xunit`.

Projects:

- `Sherland.Aspire.AppHost` - Aspire AppHost wiring API and React UI
- `Sherland.Aspire.DemoApi` - minimal API with OpenTelemetry spans
- `Sherland.Aspire.Demo.Ui.React` - Vite/React frontend emitting browser OTEL spans
- `Sherland.Aspire.DemoTests` - xUnit v3 + Playwright tests using `Sherland.Aspire.Testing.Xunit`

## Run tests

1. Install frontend dependencies:
   - `cd samples/Sherland.Aspire.Demo.Ui.React`
   - `npm ci`
2. Install the Playwright Chromium browser (first run only, or after a Playwright version
   bump): `pwsh scripts/Install-PlaywrightDependencies.ps1` from the repo root.
3. Run the full sample tests:
   - `cd samples`
   - `dotnet test Sherland.Aspire.DemoTests/Sherland.Aspire.DemoTests.csproj -c Debug -v minimal`

## Run explicit diagnostics sample test

This test is excluded by default and is intended to demonstrate failure diagnostics.

- PowerShell:
  - `$env:RUN_DIAGNOSTICS_DEMO = "1"`
  - `dotnet test Sherland.Aspire.DemoTests/Sherland.Aspire.DemoTests.csproj -c Debug --filter FullyQualifiedName~DiagnosticsDemo_IntentionalFailure_ShowsDiagnostics -v minimal`

## Browser traces in the dashboard

`Sherland.Aspire.AppHost/Program.cs` wires a dedicated OTLP/HTTP endpoint (dynamic free port,
unsecured for local dev, permissive CORS) so the `demo-ui` frontend's browser-side spans
(clicks, fetch calls) reach the Aspire dashboard just like `demo-api`'s server-side spans —
not just during `Sherland.Aspire.DemoTests` (where `OtelTraceCapture.AttachBrowserCaptureAsync`
captures them independently via Playwright interception), but also when you just run the app
normally and click around in a real browser. This works out of the box — no extra setup:

```bash
cd samples/Sherland.Aspire.AppHost
aspire start
```

To verify traces are actually reaching the dashboard, open the demo UI (`aspire ps` or
`aspire describe` for the `demo-ui` URL), click around, then inspect what landed:

```bash
aspire otel traces
aspire otel spans --trace-id <id-from-the-traces-list>
```

A trace from clicking "Process" should show a single, unified chain spanning both sides —
`demo-ui: item:process` (root) → `demo-ui: HTTP POST` → `demo-api: POST /items/{id:int}/process`
→ `demo-api: items:process`.

## Troubleshooting

### `aspire start` times out on Windows, `demo-ui` (or any resource) never starts

If the AppHost log (`~/.aspire/logs/cli_*_detach-child_*.log`) shows
`failed to initialize state store ... has invalid ownership` followed by
`Service <resource> should have valid address at this point`, this might **not** be an
AppHost or resource bug. It could mean `aspire start` is running from an elevated
(Administrator/UAC) terminal: Windows gives any directory DCP creates under
`%USERPROFILE%\.dcp\state.elevated` an owner of `BUILTIN\Administrators` instead of
your user account, and DCP refuses to use a state-store directory it doesn't own —
so DCP itself dies before it can allocate ports for *any* resource.

Fix (PowerShell), then retry `aspire start`:

```powershell
aspire stop --non-interactive
New-Item -ItemType Directory -Path "$env:USERPROFILE\.dcp\state.elevated" -Force | Out-Null
$acl = Get-Acl "$env:USERPROFILE\.dcp\state.elevated"
$acl.SetOwner([System.Security.Principal.NTAccount]"$env:USERDOMAIN\$env:USERNAME")
Set-Acl "$env:USERPROFILE\.dcp\state.elevated" $acl
aspire start --non-interactive
```

Simplest long-term fix: run `aspire start`/`aspire run` from a non-elevated terminal —
the ownership mismatch only occurs under an elevated token.
