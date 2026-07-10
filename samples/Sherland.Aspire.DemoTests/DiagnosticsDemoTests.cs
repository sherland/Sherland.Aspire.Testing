using Shouldly;

namespace Sherland.Aspire.DemoTests;

[Trait("Category", "Aspire")]
[Collection("DemoTests")]
#pragma warning disable CS9113 // Parameter is unread.
public class DiagnosticsDemoTests(DemoFixture fixture)
#pragma warning restore CS9113 // Parameter is unread.
{
    public static bool RunDiagnosticsDemo =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_DIAGNOSTICS_DEMO"), "1", StringComparison.Ordinal);

    [AspireFact(
        SkipUnless = nameof(RunDiagnosticsDemo),
        SkipType = typeof(DiagnosticsDemoTests),
        Skip = "Set RUN_DIAGNOSTICS_DEMO=1 and run this test explicitly when you want to demo failure diagnostics.")]
    public Task DiagnosticsDemo_IntentionalFailure_ShowsDiagnostics()
    {
        true.ShouldBeFalse("Intentional failure for diagnostics demonstration.");
        return Task.CompletedTask;
    }
}
