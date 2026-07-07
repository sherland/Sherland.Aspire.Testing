using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sherland.Aspire.Testing.Xunit;

namespace Sherland.Aspire.DemoTests;

public sealed class DemoFixture : AspireFixtureBase
{
    private DistributedApplication? _app;

    internal DistributedApplication App => _app
        ?? throw new InvalidOperationException("Fixture not initialized.");

    internal OtelTraceCapture Traces { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        // Dashboard anonymous-access, OTLP/HTTP endpoint, and CORS configuration are set
        // unconditionally at the top of the AppHost's own Program.cs — that applies here too,
        // since DistributedApplicationTestingBuilder executes that same top-level code.
        // Note: the testing host does not honor the AppHost's requested OTLP/HTTP port (it
        // reallocates dynamically), so browser-originated spans are captured via
        // OtelTraceCapture.AttachBrowserCaptureAsync (Playwright network interception) instead
        // of relying on the export reaching a reachable dashboard endpoint.
        Environment.SetEnvironmentVariable("SHERLAND_DEMO_TRACE_TO_CONSOLE", "1");

        try
        {
            var appHost = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.Sherland_Aspire_AppHost>(
                    args: [],
                    configureBuilder: (appOptions, _) =>
                    {
                        appOptions.DisableDashboard = false;
                    });

            appHost.Services.AddLogging(BufferingLogging);

            _app = await appHost.BuildAsync();
            await _app.StartAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHERLAND_DEMO_TRACE_TO_CONSOLE", null);
        }

        BeginWatching(_app, "demo-api", "demo-ui", "demo-ui-installer", "aspire-dashboard");
        Traces = EnableTraceCapture(_app)
            ?? throw new InvalidOperationException("Trace capture unavailable — dashboard API not enabled.");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
