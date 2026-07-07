using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;
using Xunit.Internal;
using Xunit.Sdk;
using Xunit.v3;

namespace Sherland.Aspire.Testing.Xunit;

/// <summary>
/// xUnit fact variant for Aspire integration tests.
/// Use this instead of <see cref="FactAttribute"/> so failed tests automatically
/// dump buffered Aspire logs, resource state, and browser trace output to the
/// test console.
///
/// The test class must accept an <see cref="IAspireTestDiagnosticsProvider"/>
/// (e.g. a fixture that extends <see cref="AspireFixtureBase"/>) as a constructor
/// argument for diagnostics to be written on failure.
/// </summary>
[XunitTestCaseDiscoverer(typeof(AspireFactDiscoverer))]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AspireFactAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : FactAttribute(sourceFilePath, sourceLineNumber) { }

/// <summary>
/// xUnit theory variant for Aspire integration tests.
/// Use this instead of <see cref="TheoryAttribute"/> so failed tests automatically
/// dump buffered Aspire logs, resource state, and browser trace output to the
/// test console.
/// </summary>
[XunitTestCaseDiscoverer(typeof(AspireTheoryDiscoverer))]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AspireTheoryAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : TheoryAttribute(sourceFilePath, sourceLineNumber) { }

public sealed class AspireFactDiscoverer : IXunitTestCaseDiscoverer
{
    public ValueTask<IReadOnlyCollection<IXunitTestCase>> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetails(discoveryOptions, testMethod, factAttribute);
        var testCase = new AspireXunitTestCase(
            details.ResolvedTestMethod,
            details.TestCaseDisplayName,
            details.UniqueID,
            details.Explicit,
            details.SkipExceptions,
            details.SkipReason,
            details.SkipType,
            details.SkipUnless,
            details.SkipWhen,
            testMethod.Traits.ToReadWrite(StringComparer.OrdinalIgnoreCase),
            timeout: details.Timeout);
        return new([testCase]);
    }
}

public sealed class AspireTheoryDiscoverer : TheoryDiscoverer
{
    protected override ValueTask<IReadOnlyCollection<IXunitTestCase>> CreateTestCasesForDataRow(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        ITheoryAttribute theoryAttribute,
        ITheoryDataRow dataRow,
        object?[] testMethodArguments)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetails(discoveryOptions, testMethod, theoryAttribute, testMethodArguments);
        var testCase = new AspireXunitTestCase(
            details.ResolvedTestMethod,
            details.TestCaseDisplayName,
            details.UniqueID,
            details.Explicit,
            details.SkipExceptions,
            details.SkipReason,
            details.SkipType,
            details.SkipUnless,
            details.SkipWhen,
            testMethod.Traits.ToReadWrite(StringComparer.OrdinalIgnoreCase),
            testMethodArguments,
            timeout: details.Timeout);
        return new([testCase]);
    }

    protected override ValueTask<IReadOnlyCollection<IXunitTestCase>> CreateTestCasesForTheory(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        ITheoryAttribute theoryAttribute)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetails(discoveryOptions, testMethod, theoryAttribute);
        // Unconditionally-skipped theory uses a plain XunitTestCase (no diagnostics needed)
        IXunitTestCase testCase = details.SkipReason is not null && details.SkipUnless is null && details.SkipWhen is null
            ? new XunitTestCase(
                details.ResolvedTestMethod,
                details.TestCaseDisplayName,
                details.UniqueID,
                details.Explicit,
                details.SkipExceptions,
                details.SkipReason,
                details.SkipType,
                details.SkipUnless,
                details.SkipWhen,
                testMethod.Traits.ToReadWrite(StringComparer.OrdinalIgnoreCase),
                timeout: details.Timeout)
            : new AspireXunitDelayEnumeratedTestCase(
                details.ResolvedTestMethod,
                details.TestCaseDisplayName,
                details.UniqueID,
                details.Explicit,
                theoryAttribute.SkipTestWithoutData,
                details.SkipExceptions,
                details.SkipReason,
                details.SkipType,
                details.SkipUnless,
                details.SkipWhen,
                testMethod.Traits.ToReadWrite(StringComparer.OrdinalIgnoreCase),
                timeout: details.Timeout);
        return new([testCase]);
    }
}

/// <summary>
/// Custom xUnit v3 test case for fact-style and pre-enumerated theory row Aspire tests.
/// Wraps test execution with Aspire diagnostics capture, writing resource state, logs,
/// and browser console output to the test's output on failure.
/// </summary>
public sealed class AspireXunitTestCase : XunitTestCase, ISelfExecutingXunitTestCase
{
    // xUnit v3 requires a public parameterless constructor to reconstruct test cases
    // from serialized state (VS Test Explorer, dotnet test --filter, etc.).
    // The [Obsolete] attribute is xUnit's own convention for marking it as
    // "deserializer use only" — this constructor must not be removed.
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; must not be removed.")]
    public AspireXunitTestCase() { }

    public AspireXunitTestCase(
        IXunitTestMethod testMethod,
        string testCaseDisplayName,
        string uniqueID,
        bool @explicit,
        Type[]? skipExceptions = null,
        string? skipReason = null,
        Type? skipType = null,
        string? skipUnless = null,
        string? skipWhen = null,
        Dictionary<string, HashSet<string>>? traits = null,
        object?[]? testMethodArguments = null,
        string? sourceFilePath = null,
        int? sourceLineNumber = null,
        int? timeout = null)
        : base(testMethod, testCaseDisplayName, uniqueID, @explicit, skipExceptions,
               skipReason, skipType, skipUnless, skipWhen, traits,
               testMethodArguments, sourceFilePath, sourceLineNumber, timeout) { }

    public ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource) =>
            AspireTestCaseRunner.Instance.Run(
                this,
                messageBus,
                aggregator.Clone(),
                cancellationTokenSource,
                TestCaseDisplayName,
                SkipReason,
                explicitOption,
                constructorArguments);
}

/// <summary>
/// Custom xUnit v3 test case for delay-enumerated theory Aspire tests
/// (non-serializable data or pre-enumeration disabled).
/// </summary>
public sealed class AspireXunitDelayEnumeratedTestCase : XunitDelayEnumeratedTheoryTestCase, ISelfExecutingXunitTestCase
{
    // See the note on AspireXunitTestCase() — same reason.
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; must not be removed.")]
    public AspireXunitDelayEnumeratedTestCase() { }

    public AspireXunitDelayEnumeratedTestCase(
        IXunitTestMethod testMethod,
        string testCaseDisplayName,
        string uniqueID,
        bool @explicit,
        bool skipTestWithoutData,
        Type[]? skipExceptions = null,
        string? skipReason = null,
        Type? skipType = null,
        string? skipUnless = null,
        string? skipWhen = null,
        Dictionary<string, HashSet<string>>? traits = null,
        string? sourceFilePath = null,
        int? sourceLineNumber = null,
        int? timeout = null)
        : base(testMethod, testCaseDisplayName, uniqueID, @explicit, skipTestWithoutData,
               skipExceptions, skipReason, skipType, skipUnless, skipWhen, traits,
               sourceFilePath, sourceLineNumber, timeout) { }

    public ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource) =>
            AspireTestCaseRunner.Instance.Run(
                this,
                messageBus,
                aggregator.Clone(),
                cancellationTokenSource,
                TestCaseDisplayName,
                SkipReason,
                explicitOption,
                constructorArguments);
}

/// <summary>
/// Test case runner that sets up an Aspire diagnostics scope for each test and
/// delegates execution to <see cref="AspireXunitTestRunner"/>.
/// </summary>
internal sealed class AspireTestCaseRunner
    : XunitTestCaseRunnerBase<AspireTestCaseRunnerContext, IXunitTestCase, IXunitTest>
{
    public static AspireTestCaseRunner Instance { get; } = new();

    public async ValueTask<RunSummary> Run(
        IXunitTestCase testCase,
        IMessageBus messageBus,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        string displayName,
        string? skipReason,
        ExplicitOption explicitOption,
        object?[] constructorArguments)
    {
        var tests = await aggregator.RunAsync(testCase.CreateTests, []);

        if (aggregator.ToException() is Exception ex)
        {
            if (ex.Message.StartsWith(DynamicSkipToken.Value, StringComparison.Ordinal))
                return XunitRunnerHelper.SkipTestCases(
                    messageBus, cancellationTokenSource, [testCase],
                    ex.Message[DynamicSkipToken.Value.Length..], sendTestCaseMessages: false);
            return XunitRunnerHelper.FailTestCases(
                messageBus, cancellationTokenSource, [testCase], ex, sendTestCaseMessages: false);
        }

        await using var ctxt = new AspireTestCaseRunnerContext(
            testCase, tests, messageBus, aggregator, cancellationTokenSource,
            displayName, skipReason, explicitOption, constructorArguments);
        await ctxt.InitializeAsync();
        return await Run(ctxt);
    }

    protected override async ValueTask<RunSummary> RunTest(
        AspireTestCaseRunnerContext ctxt,
        IXunitTest test)
    {
        // Record the test start time before the scope is opened, so the timestamp
        // reflects the actual moment the test body begins executing.
        AspireTestTimingContext.BeginTest();

        // Set up a per-test Aspire diagnostics scope. The scope is stored in
        // KeyValueStorage so AspireXunitTestRunner can retrieve it in OnTestFailed.
        using var diagnosticsScope = AspireDiagnosticsContext.BeginScope();
        TestContext.Current.KeyValueStorage[AspireXunitTestRunner.ScopeKey] = diagnosticsScope;

        try
        {
            return await AspireXunitTestRunner.Instance.Run(
                test,
                ctxt.MessageBus,
                ctxt.ConstructorArguments,
                ctxt.ExplicitOption,
                ctxt.Aggregator.Clone(),
                ctxt.CancellationTokenSource,
                ctxt.BeforeAfterTestAttributes);
        }
        finally
        {
            TestContext.Current.KeyValueStorage.TryRemove(AspireXunitTestRunner.ScopeKey, out _);
        }
    }
}

internal sealed class AspireTestCaseRunnerContext(
    IXunitTestCase testCase,
    IReadOnlyCollection<IXunitTest> tests,
    IMessageBus messageBus,
    ExceptionAggregator aggregator,
    CancellationTokenSource cancellationTokenSource,
    string displayName,
    string? skipReason,
    ExplicitOption explicitOption,
    object?[] constructorArguments)
    : XunitTestCaseRunnerBaseContext<IXunitTestCase, IXunitTest>(
        testCase, tests, messageBus, aggregator, cancellationTokenSource,
        displayName, skipReason, explicitOption, constructorArguments) { }

/// <summary>
/// Extends the standard xUnit test runner to append Aspire diagnostics to the
/// output of any failing test.
/// </summary>
internal sealed class AspireXunitTestRunner : XunitTestRunner
{
    internal const string ScopeKey = "Sherland.Aspire.Testing.Xunit.DiagnosticsScope";

    private AspireXunitTestRunner() { }
    public static new AspireXunitTestRunner Instance { get; } = new();

    protected override async ValueTask<(bool Continue, TestResultState ResultState)> OnTestFailed(
        XunitTestRunnerContext ctxt,
        Exception exception,
        decimal executionTime,
        string output,
        string[]? warnings)
    {
        var enrichedOutput = output;

        if (TestContext.Current.KeyValueStorage.TryGetValue(ScopeKey, out var scopeObj)
            && scopeObj is BrowserDiagnosticsContextScope diagnosticsScope
            && ctxt.ConstructorArguments.OfType<IAspireTestDiagnosticsProvider>().FirstOrDefault() is { } provider)
        {
            var sb = new StringBuilder(output);
            await provider.WriteDiagnosticsAsync(
                ctxt.Test.TestDisplayName,
                diagnosticsScope.BrowserScope,
                line => sb.AppendLine(line));
            enrichedOutput = sb.ToString();
        }

        return await base.OnTestFailed(ctxt, exception, executionTime, enrichedOutput, warnings);
    }
}

