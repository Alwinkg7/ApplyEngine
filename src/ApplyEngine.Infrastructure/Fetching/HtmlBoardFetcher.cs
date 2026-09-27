using ApplyEngine.Domain.Entities;
using HtmlAgilityPack;

namespace ApplyEngine.Infrastructure.Fetching;

/// <summary>
/// Per-board selector config, read from appsettings.json under
/// HtmlBoards:{SourceName}. XPath, not CSS selectors — HtmlAgilityPack's
/// native query language, and precise enough for board layouts that don't
/// expose clean class names.
///
/// IMPORTANT: this sandbox has no outbound access to job-board sites, so
/// these XPaths are illustrative, not verified against the real Technopark /
/// Infopark markup. Open the board in a browser, use "Inspect Element" on one
/// listing, and update the XPaths in appsettings.json to match — see README.md
/// "Wiring up a real HTML board" for the walkthrough.
/// </summary>
public class BoardSelectorConfig
{
    public required string ItemXPath { get; set; }
    public required string TitleXPath { get; set; }
    public required string CompanyXPath { get; set; }
    public string? LocationXPath { get; set; }
    public required string LinkXPath { get; set; }
    public string? LinkAttribute { get; set; } = "href";

    /// <summary>Prepended to relative hrefs found by LinkXPath (e.g. "https://www.technopark.org").</summary>
    public string? BaseUrl { get; set; }
}

public class HtmlBoardFetcher : ISourceFetcher
{
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, BoardSelectorConfig> _selectorsBySourceName;

    public SourceType HandlesType => SourceType.HtmlBoard;

    public HtmlBoardFetcher(HttpClient http, IReadOnlyDictionary<string, BoardSelectorConfig> selectorsBySourceName)
    {
        _http = http;
        _selectorsBySourceName = selectorsBySourceName;
    }

    public async Task<List<Job>> FetchAsync(Source source, CancellationToken ct = default)
    {
        if (!_selectorsBySourceName.TryGetValue(source.Name, out var config))
            throw new InvalidOperationException(
                $"No HtmlBoards:{source.Name} selector config found in appsettings.json. " +
                "Add ItemXPath/TitleXPath/CompanyXPath/LinkXPath for this board before enabling it.");

        var html = await _http.GetStringAsync(source.Endpoint, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var jobs = new List<Job>();
        var items = doc.DocumentNode.SelectNodes(config.ItemXPath);
        if (items is null) return jobs;

        foreach (var item in items)
        {
            var title = item.SelectSingleNode(config.TitleXPath)?.InnerText?.Trim();
            var company = item.SelectSingleNode(config.CompanyXPath)?.InnerText?.Trim() ?? source.Name;
            var linkNode = item.SelectSingleNode(config.LinkXPath);
            var link = config.LinkAttribute is null
                ? linkNode?.InnerText?.Trim()
                : linkNode?.GetAttributeValue(config.LinkAttribute, null);

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;

            if (config.BaseUrl is not null && link.StartsWith('/'))
                link = config.BaseUrl.TrimEnd('/') + link;

            var location = config.LocationXPath is null
                ? null
                : item.SelectSingleNode(config.LocationXPath)?.InnerText?.Trim();

            jobs.Add(new Job
            {
                Title = title,
                Company = company,
                Location = location,
                Url = link,
                RawText = item.InnerText,
                SourceName = source.Name,
            });
        }

        return jobs;
    }
}
