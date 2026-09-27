using ApplyEngine.Domain.Entities;
using ApplyEngine.Domain.Feeds;

namespace ApplyEngine.Infrastructure.Fetching;

/// <summary>
/// Fetches an RSS/Atom URL and hands the XML to the dependency-free
/// ApplyEngine.Domain.Feeds.RssFeedParser (verified in tools/ApplyEngine.SelfCheck).
/// This class's only job is the HTTP call — all parsing logic lives in Domain.
/// </summary>
public class RssSourceFetcher : ISourceFetcher
{
    private readonly HttpClient _http;
    public SourceType HandlesType => SourceType.Rss;

    public RssSourceFetcher(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<Job>> FetchAsync(Source source, CancellationToken ct = default)
    {
        var xml = await _http.GetStringAsync(source.Endpoint, ct);
        return RssFeedParser.Parse(xml, source.Name);
    }
}
