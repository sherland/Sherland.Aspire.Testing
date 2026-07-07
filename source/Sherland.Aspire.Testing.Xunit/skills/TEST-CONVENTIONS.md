---
name: sherland-aspire-test-conventions
description: Enforce AspireFact/AspireTheory usage and controlled execution patterns in integration tests.
packages: Sherland.Aspire.Testing.Xunit
---

# Enforce Test Conventions

Use this workflow to keep integration tests consistent and diagnostics-enabled.

## Rules

1. Use `[AspireFact]` / `[AspireTheory]` instead of plain xUnit attributes.
2. Use `SkipUnless`/`SkipWhen` for environment-gated tests.
3. Use `Explicit = true` for manual-only tests.

## Convention Guard Test

```csharp
[AspireFact]
public void AllTests_MustUseAspireFactOrAspireTheory()
{
    var assembly = typeof(AppFixture).Assembly;

    var violations = assembly
        .GetTypes()
        .Where(t => t.IsClass && t.Namespace == "MyApp.IntegrationTests")
        .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static))
        .Where(m => m.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(FactAttribute) ||
            a.AttributeType == typeof(TheoryAttribute)))
        .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    Assert.Empty(violations);
}
```
