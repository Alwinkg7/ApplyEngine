using System.Xml.Linq;
using ApplyEngine.Domain.Entities;

namespace ApplyEngine.Domain.Feeds;

/// <summary>
/// Parses RSS 2.0 and Atom feeds into Jobs using System.Xml.Linq only —
/// deliberately not System.ServiceModel.Syndication (a NuGet package) so
/// this class has zero external dependencies and can be exercised by
/// tools/ApplyEngine.SelfCheck with no package restore at all.
///
/// This is the parser behind the "friendly sources" ingestion lane in spec
/// §3 Layer 1 — any RSS-emitting job board or feed aggregator plugs in here.
/// </summary>
public static class RssFeedParser
{
    public static List<Job> Parse(string xml, string sourceName)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidOperationException("Empty feed document.");

        return root.Name.LocalName == "feed"
            ? ParseAtom(root, sourceName)
            : ParseRss2(root, sourceName);
    }

    private static List<Job> ParseRss2(XElement root, string sourceName)
    {
        var channel = root.Element("channel");
        if (channel is null) return new List<Job>();

        var jobs = new List<Job>();
        foreach (var item in channel.Elements("item"))
        {
            var title = item.Element("title")?.Value?.Trim();
            var link = item.Element("link")?.Value?.Trim();
            var description = item.Element("description")?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;

            var (parsedTitle, company) = SplitTitleAndCompany(title);
            var pubDateText = item.Element("pubDate")?.Value;

            jobs.Add(new Job
            {
                Title = parsedTitle,
                Company = company,
                Url = link,
                RawText = description,
                SourceName = sourceName,
                PostedAtUtc = TryParseDate(pubDateText),
            });
        }
        return jobs;
    }

    private static List<Job> ParseAtom(XElement root, string sourceName)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var jobs = new List<Job>();

        foreach (var entry in root.Elements(atom + "entry"))
        {
            var title = entry.Element(atom + "title")?.Value?.Trim();
            var link = entry.Elements(atom + "link")
                .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")
                ?.Attribute("href")?.Value;
            var summary = entry.Element(atom + "summary")?.Value?.Trim()
                          ?? entry.Element(atom + "content")?.Value?.Trim()
                          ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;

            var (parsedTitle, company) = SplitTitleAndCompany(title);
            var updatedText = entry.Element(atom + "updated")?.Value ?? entry.Element(atom + "published")?.Value;

            jobs.Add(new Job
            {
                Title = parsedTitle,
                Company = company,
                Url = link,
                RawText = summary,
                SourceName = sourceName,
                PostedAtUtc = TryParseDate(updatedText),
            });
        }
        return jobs;
    }

    /// <summary>
    /// Many job feeds format the title as "Role at Company" or "Role - Company".
    /// Falls back to the feed/source name as the company when no separator is found.
    /// </summary>
    private static (string Title, string Company) SplitTitleAndCompany(string rawTitle)
    {
        foreach (var separator in new[] { " at ", " @ ", " - ", " | " })
        {
            var idx = rawTitle.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return (rawTitle[..idx].Trim(), rawTitle[(idx + separator.Length)..].Trim());
        }
        return (rawTitle, "Unknown");
    }

    private static DateTime? TryParseDate(string? text) =>
        !string.IsNullOrWhiteSpace(text) && DateTime.TryParse(text, out var dt)
            ? dt.ToUniversalTime()
            : null;
}
