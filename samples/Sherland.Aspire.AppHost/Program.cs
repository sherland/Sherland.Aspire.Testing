// Give the dashboard a genuine OTLP/HTTP endpoint so the browser's OTEL JS SDK — which can
// only export over HTTP, not gRPC — can actually reach it. This isn't configured by default
// when the dashboard is launched via the AppHost (only OTLP/gRPC is), and the dashboard's
// frontend endpoint does not itself accept OTLP traffic despite matching the /v1/traces route
// (it responds "Connection types 'OtlpGrpc, OtlpHttp' are not enabled on this connection").
// Local-dev only: unsecured dashboard access and permissive CORS, matching Aspire's own
// guidance that these settings are for local development only.
var otlpHttpEndpointUrl = $"http://localhost:{GetFreeTcpPort()}";
Environment.SetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "true");
Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS", "true");
Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL", otlpHttpEndpointUrl);
Environment.SetEnvironmentVariable("Dashboard__Otlp__Cors__AllowedOrigins", "*");
Environment.SetEnvironmentVariable("Dashboard__Otlp__Cors__AllowedHeaders", "*");

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sherland_Aspire_DemoApi>("demo-api")
    // Standard OTEL env var: lowers the batch span processor's scheduled export delay from
    // its 5000ms default so spans reach the dashboard promptly — both for snappier demo
    // feedback and so trace-assertion tests aren't racing the default batch interval.
    .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "200");

var ui = builder.AddViteApp("demo-ui", "../Sherland.Aspire.Demo.Ui.React")
    .WithReference(api)
    .WithEnvironment("VITE_API_BASE_URL", api.GetEndpoint("http"))
    .WithEnvironment("VITE_OTEL_EXPORTER_OTLP_ENDPOINT", otlpHttpEndpointUrl);

if (Environment.GetEnvironmentVariable("SHERLAND_DEMO_TRACE_TO_CONSOLE") == "1")
{
    api.WithEnvironment("SHERLAND_DEMO_TRACE_TO_CONSOLE", "1");
    ui.WithEnvironment("VITE_OTEL_TRACE_TO_CONSOLE", "1");
}

ui.WithEnvironment(ctx =>
{
    if (ctx.ExecutionContext.IsRunMode &&
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS") is { Length: > 0 } headers)
    {
        ctx.EnvironmentVariables["VITE_OTEL_EXPORTER_OTLP_HEADERS"] = headers;
    }
});

builder.Build().Run();

static int GetFreeTcpPort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

public partial class Program { }
