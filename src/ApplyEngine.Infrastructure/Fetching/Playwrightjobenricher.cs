using ApplyEngine.Domain.Processing;
using Microsoft.Playwright;
using Serilog;

namespace ApplyEngine.Infrastructure.Fetching;

/// <summary>
/// Replaces the original HttpClient-based JobPageEnricher. Real diagnostic
/// logs from a live run proved plain HTTP requests get blocked outright:
/// LinkedIn redirects every anonymous job-view URL to its login wall
/// (identical 53KB response every time, regardless of User-Agent), and
/// Indeed's click-tracking redirect returns 403 Forbidden on essentially
/// every request. A real headless browser has a more realistic fingerprint
/// (executes JS, has real TLS/HTTP2 client behavior) and stands a better
/// chance — though LinkedIn's login wall may still show up for postings that
/// require an authenticated session even with a real browser (there's no
/// logged-in LinkedIn account behind this). Set honest expectations with
/// yourself, not just the user, before assuming this "fixes" enrichment.
///
/// Deliberately mirrors PlaywrightBoardFetcher's lazy-launch-once,
/// new-context-per-page pattern: one Chromium process is reused across every
/// job in a run (expensive to launch), but each job gets its own
/// BrowserContext (cheap) so cookies/storage from one job's page never leak
/// into the next — relevant if LinkedIn ever sets tracking cookies that
/// change behavior across requests.
///
/// Same "every layer degrades independently" rule as the HTTP version: any
/// failure (timeout, navigation error, blocked, login wall) returns null and
/// is logged at Debug — never throws, never blocks the pipeline.
/// </summary>
public class PlaywrightJobEnricher : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task<JobEnrichmentResult?> EnrichAsync(string url, CancellationToken ct = default)
    {
        try
        {
            _playwright ??= await Playwright.CreateAsync();
            _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

            await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
                Locale = "en-US",
            });
            var page = await context.NewPageAsync();

            IResponse? response;
            try
            {
                response = await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 20_000,
                });
            }
            catch (TimeoutException tex)
            {
                Log.Debug(tex, "[Enrichment] {Url} -> navigation timeout, skipped", url);
                return null;
            }
            catch (PlaywrightException pex)
            {
                Log.Debug(pex, "[Enrichment] {Url} -> Playwright navigation error, skipped", url);
                return null;
            }

            var status = response?.Status ?? 0;

            // Same "dead link = closed" signal as the HTTP version.
            if (status is 404 or 410)
            {
                Log.Debug("[Enrichment] {Url} -> {Status} (treated as closed)", url, status);
                return new JobEnrichmentResult(null, true);
            }

            if (status != 0 && status >= 400)
            {
                Log.Debug("[Enrichment] {Url} -> {Status} (skipped, not parsed)", url, status);
                return null;
            }

            // Some boards finish injecting their JSON-LD script tag slightly
            // after DOMContentLoaded fires — give it a brief, bounded chance
            // to settle rather than parsing a half-rendered page. Not fatal
            // if it times out; we just parse whatever's there.
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
                // fine — parse whatever loaded so far
            }

            var finalUrl = page.Url;

            // A real browser still lands on LinkedIn's login wall (or
            // Indeed's equivalent challenge page) for postings that require
            // an authenticated session — that's an auth barrier, not
            // something a better fingerprint gets around. Detect it by the
            // landing URL so we don't misparse a login page as "no signal
            // found" and silently do nothing useful with it.
            var loginWalled =
                finalUrl.Contains("/uas/login", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("/authwall", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("/checkpoint/", StringComparison.OrdinalIgnoreCase);

            if (loginWalled)
            {
                Log.Debug("[Enrichment] {Url} -> redirected to login/auth wall at {FinalUrl}, skipped", url, finalUrl);
                return null;
            }

            var html = await page.ContentAsync();
            var result = JobEnrichmentParser.ParseHtml(html);

            Log.Debug("[Enrichment] {Url} -> {Status} via {FinalUrl}, {HtmlLength} bytes, company={Company}, closed={IsClosed}",
                url, status, finalUrl, html.Length, result.Company ?? "(none found)", result.IsClosed);

            return result;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Enrichment] {Url} -> exception, skipped", url);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}