using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Playwright;
using Shouldly;

namespace Sherland.Aspire.DemoTests;

[Trait("Category", "Aspire")]
[Collection("DemoTests")]
public class BrowserTraceTests(DemoFixture fixture) : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private async Task<IPage> OpenPageAsync()
    {
        var uri = fixture.App.GetEndpoint("demo-ui");
        var page = AspirePageDiagnostics.Track(await _context!.NewPageAsync());
        await page.GotoAsync(
            $"{uri}dashboard/",
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 30_000 });
        await page.GetByTestId("items-list").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20_000,
        });
        return page;
    }

    [AspireFact]
    public async Task ItemsPage_LoadsAndRendersList()
    {
        var page = await OpenPageAsync();
        var count = await page.Locator("[data-testid^='item-']").CountAsync();
        count.ShouldBe(5);
    }

    [AspireFact]
    public async Task ProcessButton_EmitsSingleNamedRootSpan()
    {
        var page = await OpenPageAsync();
        await using var browserCapture = await fixture.Traces.AttachBrowserCaptureAsync(page);
        fixture.Traces.Reset();

        await page.GetByTestId("process-item-1").ClickAsync();
        await page.GetByTestId("process-result").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });

        await fixture.Traces.TriggerBrowserFlushAsync(page);
        await fixture.Traces.WaitForAsync(
            spans => spans.Any(s => s.IsRoot && s.Name == "item:process"),
            timeout: TimeSpan.FromSeconds(10));

        fixture.Traces.AssertSingleRootSpan(s => s.Name == "item:process");
        fixture.Traces.AssertNoOrphanedSpans();
    }

    [AspireFact]
    public async Task ApiCallAndBrowserClick_EmitExactlyTwoRootSpans()
    {
        var page = await OpenPageAsync();
        await using var browserCapture = await fixture.Traces.AttachBrowserCaptureAsync(page);
        fixture.Traces.Reset();

        using var client = fixture.App.CreateHttpClient("demo-api");
        var apiResponse = await client.PostAsync("/items/4/process", content: null);
        apiResponse.EnsureSuccessStatusCode();

        await page.GetByTestId("process-item-2").ClickAsync();
        await page.GetByTestId("process-result").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000,
        });
        await fixture.Traces.TriggerBrowserFlushAsync(page);

        await fixture.Traces.WaitForAsync(
            spans => spans.Count(s => s.IsRoot) >= 2,
            timeout: TimeSpan.FromSeconds(10));

        fixture.Traces.AssertRootSpanCount(2);
        fixture.Traces.AssertNoOrphanedSpans();
    }
}
