using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using System.Net;
using System.Net.Http.Json;

namespace Sherland.Aspire.DemoTests;

[Trait("Category", "Aspire")]
[Collection("DemoTests")]
public class ApiTests(DemoFixture fixture)
{
    [AspireFact]
    public async Task GetItems_ReturnsFiveItems()
    {
        using var client = fixture.App.CreateHttpClient("demo-api");

        var response = await client.GetAsync("/items");
        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync<List<DemoItemResponse>>();
        items.ShouldNotBeNull();
        items.Count.ShouldBe(5);
        items[0].Name.ShouldNotBeNullOrWhiteSpace();
    }

    [AspireFact]
    public async Task ProcessItem_ReturnsProcessedTrue()
    {
        using var client = fixture.App.CreateHttpClient("demo-api");

        var response = await client.PostAsync("/items/1/process", content: null);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ProcessResponse>();
        body.ShouldNotBeNull();
        body.Processed.ShouldBeTrue();
        body.Id.ShouldBe(1);
    }

    [AspireFact]
    public async Task ProcessItem_EmitsSingleRootSpanForDirectApiCall()
    {
        fixture.Traces.Reset();

        using var client = fixture.App.CreateHttpClient("demo-api");
        var response = await client.PostAsync("/items/3/process", content: null);
        response.EnsureSuccessStatusCode();

        await fixture.Traces.WaitForAsync(
            spans => spans.Any(s => s.IsRoot && s.Name.Contains("process", StringComparison.OrdinalIgnoreCase)),
            timeout: TimeSpan.FromSeconds(10));

        fixture.Traces.AssertRootSpanCount(1, s => s.Name.Contains("/items/{id:int}/process", StringComparison.OrdinalIgnoreCase));
        fixture.Traces.AssertAllDirectChildren(
            parentPredicate: s => s.IsRoot,
            childPredicate: s => s.Name == "items:process");
        fixture.Traces.AssertNoOrphanedSpans();
        fixture.Traces.AssertNoErrorSpans();
    }

    [AspireFact]
    public async Task FailItem_EmitsErrorSpan()
    {
        fixture.Traces.Reset();

        using var client = fixture.App.CreateHttpClient("demo-api");
        var response = await client.PostAsync("/items/5/fail", content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        await fixture.Traces.WaitForAsync(
            spans => spans.Any(s => s.Name == "items:fail"),
            timeout: TimeSpan.FromSeconds(10));

        fixture.Traces.AssertHasErrorSpan(s => s.Name == "items:fail");
    }

    public sealed record DemoItemResponse(int Id, string Name, string Category);
    public sealed record ProcessResponse(bool Processed, int Id, string Message);
}
