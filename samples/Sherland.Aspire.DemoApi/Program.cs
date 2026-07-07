using System.Diagnostics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(
	null,
	LogLevel.None);

builder.AddDemoServiceDefaults();

builder.Services.AddCors(options =>
{
	options.AddDefaultPolicy(policy =>
		policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true));
});

var app = builder.Build();

app.UseCors();
app.MapDefaultEndpoints();

var demoActivitySource = new ActivitySource("Sherland.Aspire.DemoApi");

var items = new[]
{
	new DemoItem(1, "Customer records sync", "integration"),
	new DemoItem(2, "Invoice parser", "automation"),
	new DemoItem(3, "Reference score engine", "analytics"),
	new DemoItem(4, "Tender document index", "search"),
	new DemoItem(5, "Report exporter", "reporting"),
};

app.MapGet("/items", () =>
{
	using var span = demoActivitySource.StartActivity("items:list", ActivityKind.Internal);
	span?.SetTag("items.count", items.Length);
	return Results.Ok(items);
});

app.MapPost("/items/{id:int}/process", async (int id, CancellationToken cancellationToken) =>
{
	var item = items.FirstOrDefault(x => x.Id == id);
	if (item is null)
	{
		return Results.NotFound(new { message = $"Item {id} was not found" });
	}

	using var span = demoActivitySource.StartActivity("items:process", ActivityKind.Internal);
	span?.SetTag("item.id", id);
	span?.SetTag("item.category", item.Category);

	await Task.Delay(50, cancellationToken);

	return Results.Ok(new
	{
		processed = true,
		id,
		message = $"Processed item {id} ({item.Name})"
	});
});

app.MapPost("/items/{id:int}/fail", (int id) =>
{
	using var span = demoActivitySource.StartActivity("items:fail", ActivityKind.Internal);
	span?.SetTag("item.id", id);

	var exception = new InvalidOperationException($"Simulated failure processing item {id}");
	span?.AddException(exception);
	span?.SetStatus(ActivityStatusCode.Error, exception.Message);

	return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError);
});

app.Run();

public sealed record DemoItem(int Id, string Name, string Category);
