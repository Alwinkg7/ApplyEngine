using ApplyEngine.Domain.Entities;
using Microsoft.Playwright;

namespace ApplyEngine.Infrastructure.Fetching;

/// <summary>
/// Per-board selector config for JS-rendered boards. CSS selectors (Playwright's
/// native query language), not XPath — see BoardSelectorConfig for the
/// HtmlAgilityPack/XPath equivalent used on server-rendered boards.
///
/// IMPORTANT: this sandbox has no outbound access to infoparkdaily.online, and
/// WebFetch confirmed the page is a client-rendered shell it cannot execute JS
/// against either — so these selectors are illustrative only, not verified.
/// Open the real page in a browser, inspect one listing, and update
/// appsettings.json before enabling this source. See README "Wiring up a
/// JS-rendered board (Playwright)".
/// </summary>
public class PlaywrightBoardSelectorConfig
{
    public required string ItemSelector { get; set; }
    public required string TitleSelector { get; set; }
    public string? CompanySelector { get; set; }
    public string? LocationSelector { get; set; }
    public required string LinkSelector { get; set; }

    /// <summary>A selector Playwright waits to appear before scraping — proof the JS finished rendering job cards, not just that the page loaded.</summary>
    public string? WaitForSelector { get; set; }

    public string? BaseUrl { get; set; }
}

/// <summary>
/// Fetches a JS-rendered board using a real headless browser. This is
/// deliberately the same tool (Playwright) the spec scopes for Phase 3's
/// portal submission engine — a board that needs a browser to *read* is a
/// preview of the browser-automation discipline that submitting will need
/// later: explicit waits, no blind timing assumptions, fail loud rather than
/// scrape garbage silently.
/// </summary>
public class PlaywrightBoardFetcher : ISourceFetcher, IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, PlaywrightBoardSelectorConfig> _selectorsBySourceName;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public SourceType HandlesType => SourceType.JsRenderedBoard;

    public PlaywrightBoardFetcher(IReadOnlyDictionary<string, PlaywrightBoardSelectorConfig> selectorsBySourceName)
    {
        _selectorsBySourceName = selectorsBySourceName;
    }

    public async Task<List<Job>> FetchAsync(Source source, CancellationToken ct = default)
    {
        if (!_selectorsBySourceName.TryGetValue(source.Name, out var config))
            throw new InvalidOperationException(
                $"No JsRenderedBoards:{source.Name} selector config found in appsettings.json.");

        _playwright ??= await Playwright.CreateAsync();
        _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (compatible; ApplyEngine-Phase0/0.1; +personal job search tool)",
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(source.Endpoint, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });

        if (config.WaitForSelector is not null)
            await page.WaitForSelectorAsync(config.WaitForSelector, new PageWaitForSelectorOptions { Timeout = 15_000 });

        var jobs = new List<Job>();
        var items = await page.QuerySelectorAllAsync(config.ItemSelector);

        foreach (var item in items)
        {
            var titleEl = await item.QuerySelectorAsync(config.TitleSelector);
            var title = titleEl is null ? null : (await titleEl.InnerTextAsync()).Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            var company = await InnerTextOrDefaultAsync(item, config.CompanySelector) ?? source.Name;
            var location = await InnerTextOrDefaultAsync(item, config.LocationSelector);

            var linkEl = await item.QuerySelectorAsync(config.LinkSelector);
            var link = await (linkEl?.GetAttributeAsync("href") ?? Task.FromResult<string?>(null));
            if (string.IsNullOrWhiteSpace(link)) continue;
            if (config.BaseUrl is not null && link.StartsWith('/'))
                link = config.BaseUrl.TrimEnd('/') + link;

            var rawText = (await item.InnerTextAsync()).Trim();

            jobs.Add(new Job
            {
                Title = title,
                Company = company,
                Location = location,
                Url = link,
                RawText = rawText,
                SourceName = source.Name,
            });
        }

        return jobs;
    }

    private static async Task<string?> InnerTextOrDefaultAsync(IElementHandle scope, string? selector)
    {
        if (selector is null) return null;
        var el = await scope.QuerySelectorAsync(selector);
        if (el is null) return null;
        var text = await el.InnerTextAsync();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
