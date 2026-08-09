# Agent Guide: Sherland.Aspire.Testing

This repo is an Aspire 13.4 distributed app (AppHost at
`samples/Sherland.Aspire.AppHost`). Use the following instead of ad-hoc
`dotnet run` / `npx vite` / `curl` workflows.

## Starting, stopping, and inspecting the app

Use the `aspire` skill (routes to `aspire-orchestration` for lifecycle,
`aspire-monitoring` for logs/traces/state):

- `aspire start --non-interactive` — start the AppHost in the background. Never
  `dotnet run` the AppHost directly.
- `aspire wait <resource> --non-interactive` — wait for a resource to be healthy
  instead of polling with curl.
- `aspire ps` / `aspire describe <resource> --format Json` — check status and
  get resource endpoints (e.g. the demo-ui URL) instead of guessing ports.
- `aspire stop --non-interactive` — always stop before running `dotnet test`,
  since the test suite (`Sherland.Aspire.DemoTests`) spins up its own AppHost
  instance via `DemoFixture`/`[AspireFact]`; a manually-started instance left
  running can collide with it on ports or npm/node_modules file locks.

If `aspire start` times out on Windows with a DCP state-store ownership error,
see the recovery steps in the `aspire-orchestration` skill (elevated/UAC shells
only).

## Driving/inspecting the app in a browser

Use the `playwright-cli` skill (or `playwright-cli.cmd` directly) to navigate
pages, click elements, and read console/network output, rather than writing
one-off Playwright scripts or curling HTML by hand.

## Running tests

`dotnet test samples/Sherland.Aspire.DemoTests` — this project manages its own
AppHost lifecycle per test collection; don't `aspire start` first (see above).
