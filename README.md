# Sherland.Aspire.Testing

This repository contains:

- `source/Sherland.Aspire.Testing.Xunit` - the NuGet package project
- `samples` - a self-contained Aspire sample suite that exercises the package

## Package project

Path: `source/Sherland.Aspire.Testing.Xunit`

Build:

```powershell
dotnet build source/Sherland.Aspire.Testing.Xunit/Sherland.Aspire.Testing.Xunit.csproj
```

Pack:

```powershell
dotnet pack source/Sherland.Aspire.Testing.Xunit/Sherland.Aspire.Testing.Xunit.csproj -c Release
```

Primary package documentation:

- `source/Sherland.Aspire.Testing.Xunit/README.md`
- `source/Sherland.Aspire.Testing.Xunit/docs/internals.md`
- `source/Sherland.Aspire.Testing.Xunit/docs/sherland-aspire-testing-xunit-skills.md`

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
