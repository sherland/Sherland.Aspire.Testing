# Sherland.Aspire.Testing

[![CI](https://github.com/sherland/Sherland.Aspire.Testing/actions/workflows/ci.yml/badge.svg)](https://github.com/sherland/Sherland.Aspire.Testing/actions/workflows/ci.yml)
[![Package](https://img.shields.io/badge/package-GitHub%20Packages-blue)](https://github.com/sherland/Sherland.Aspire.Testing/pkgs/nuget/Sherland.Aspire.Testing.Xunit)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

`Sherland.Aspire.Testing.Xunit` is an xUnit v3 integration library for [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/aspire-overview) distributed-application tests. **Silent on success. Complete diagnostics on failure.** `[AspireFact]`/`[AspireTheory]` are drop-in replacements for `[Fact]`/`[Theory]` that automatically buffer resource logs, resource state, and browser console output — flushing them to the test console only when a test fails. `OtelTraceCapture` verifies OTEL trace shape (root-span counts, parent/child structure, error status) the same way whether the trace came from a direct API call or a Playwright browser interaction.

This repository contains:

- `source/Sherland.Aspire.Testing.Xunit` - the NuGet package project
- `samples` - a self-contained Aspire sample suite that exercises the package

## Package project

Path: `source/Sherland.Aspire.Testing.Xunit`

Prerequisites: .NET 10 SDK. (Building and running `samples` additionally needs Node 20 and the Playwright Chromium browser — see `samples/README.md`.)

Build:

```powershell
dotnet build source/Sherland.Aspire.Testing.Xunit/Sherland.Aspire.Testing.Xunit.csproj
```

Pack:

```powershell
dotnet pack source/Sherland.Aspire.Testing.Xunit/Sherland.Aspire.Testing.Xunit.csproj -c Release
```

Consumer-facing package documentation:

- `source/Sherland.Aspire.Testing.Xunit/README.md` - getting started, API reference (also embedded in the NuGet package)
- `source/Sherland.Aspire.Testing.Xunit/docs/internals.md` - implementation internals

Maintainer / release-process documentation:

- `source/Sherland.Aspire.Testing.Xunit/docs/sherland-aspire-testing-xunit-skills.md` - how the embedded AI skill files are versioned, packed, and validated before release

## Sample Projects

Path: `samples`

Projects:

- `samples/Sherland.Aspire.AppHost`
- `samples/Sherland.Aspire.DemoApi`
- `samples/Sherland.Aspire.Demo.Ui.React`
- `samples/Sherland.Aspire.DemoTests`

Sample instructions:

- `samples/README.md`

Run sample tests:

```powershell
cd samples
dotnet test Sherland.Aspire.DemoTests/Sherland.Aspire.DemoTests.csproj -c Debug -v minimal
```
