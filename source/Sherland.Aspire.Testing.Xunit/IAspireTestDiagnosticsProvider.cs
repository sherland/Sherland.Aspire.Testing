namespace Sherland.Aspire.Testing.Xunit;

/// <summary>
/// Implemented by test fixtures that want to participate in the failure-diagnostics
/// mechanism provided by <see cref="AspireFactAttribute"/> / <see cref="AspireTheoryAttribute"/>.
///
/// When a test fails the xUnit runner finds the fixture via this interface in the
/// test class's constructor arguments and calls <see cref="WriteDiagnosticsAsync"/>
/// so the fixture can write resource state, logs, and browser output to xUnit's
/// test output (associated with the failing test in VS Test Explorer and CI reports).
/// </summary>
public interface IAspireTestDiagnosticsProvider
{
    /// <param name="testName">Display name of the failing test.</param>
    /// <param name="browserScope">Browser diagnostics captured during the test, or <c>null</c>.</param>
    /// <param name="writeLine">Delegate that routes a line of output to xUnit's test output channel.</param>
    ValueTask WriteDiagnosticsAsync(string testName, BrowserDiagnosticsScope? browserScope, Action<string> writeLine);
}

