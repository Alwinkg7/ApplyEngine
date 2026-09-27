using System.Net;
using System.Text.Json;

namespace ApplyEngine.Domain.Processing;

public record JobEnrichmentResult(string? Company, bool IsClosed);

/// <summary>
/// Best-effort extraction of a job's real company name and open/closed
/// status from the HTML of its own posting page — this is the parsing half
/// of the Phase 1 "get real company names, skip closed roles" feature. The
/// actual HTTP GET lives in ApplyEngine.Infrastructure.Fetching.
/// JobPageEnricher; this class only parses HTML it's handed, so it stays
/// zero-dependency and testable via ApplyEngine.SelfCheck with no network
/// call and no NuGet package (same reasoning as JobNormalizer/DedupeService).
///
/// Two independent signals, checked in this order:
///   1. schema.org JobPosting JSON-LD — most job boards (LinkedIn, Indeed,
///      Naukri, ATS-backed career pages) embed this specifically so Google's
///      job search crawler can index postings, and it's served even to
///      anonymous/unauthenticated requests since it's meant for a bot, not a
///      logged-in user. hiringOrganization.name gives the real company; a
///      validThrough date in the past means the posting has expired.
///   2. A plain-text scan for common "this posting is closed" phrasing, as a
///      fallback for pages with no JSON-LD (or JSON-LD missing validThrough).
///
/// This is inherently best-effort: a board can change its markup, block
/// automated requests outright (LinkedIn in particular gates most page
/// content behind a login wall for non-browser requests), or simply never
/// say anywhere that a posting closed. A miss just leaves the result at
/// "unknown company, not closed" rather than guessing — same
/// "every layer degrades independently" principle as the rest of Phase 0/1.
/// </summary>
public static class JobEnrichmentParser
{
    private static readonly string[] ClosedPhrases =
    {
        "no longer accepting applications",
        "no longer accepting applicants",
        "no longer accepting job applications",
        "this job is no longer available",
        "this job posting is no longer active",
        "job posting has expired",
        "this posting has expired",
        "position has been filled",
        "this position has been filled",
        "closed to applications",
    };

    public static JobEnrichmentResult ParseHtml(string html)
    {
        string? company = null;
        var isClosed = false;

        foreach (var jsonLdContent in ExtractJsonLdBlocks(html))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(WebUtility.HtmlDecode(jsonLdContent));
            }
            catch (JsonException)
            {
                continue; // malformed/partial JSON-LD — try the next block
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!IsJobPostingNode(root)) continue;

                if (company is null &&
                    root.TryGetProperty("hiringOrganization", out var org) &&
                    org.TryGetProperty("name", out var nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String)
                {
                    company = nameEl.GetString();
                }

                if (!isClosed &&
                    root.TryGetProperty("validThrough", out var validThroughEl) &&
                    validThroughEl.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(validThroughEl.GetString(), out var validThrough) &&
                    validThrough.ToUniversalTime() < DateTime.UtcNow)
                {
                    isClosed = true;
                }
            }
        }

        if (!isClosed && ClosedPhrases.Any(p => html.Contains(p, StringComparison.OrdinalIgnoreCase)))
            isClosed = true;

        return new JobEnrichmentResult(company, isClosed);
    }

    /// <summary>
    /// Hand-rolled instead of a single regex over the whole document: some
    /// real pages embed JSON-LD blocks large enough (multi-KB, sometimes with
    /// nested arrays) that a greedy/lazy regex across the whole HTML string
    /// either mismatches block boundaries or falls over on catastrophic
    /// backtracking. A simple indexed scan for the open/close script tags has
    /// neither problem and needs no regex engine at all.
    /// </summary>
    private static IEnumerable<string> ExtractJsonLdBlocks(string html)
    {
        const string needle = "application/ld+json";
        var searchFrom = 0;

        while (true)
        {
            var scriptOpenTagStart = html.IndexOf("<script", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (scriptOpenTagStart < 0) yield break;

            var scriptOpenTagEnd = html.IndexOf('>', scriptOpenTagStart);
            if (scriptOpenTagEnd < 0) yield break;

            var openTag = html[scriptOpenTagStart..(scriptOpenTagEnd + 1)];
            searchFrom = scriptOpenTagEnd + 1;

            if (!openTag.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            var closeTagStart = html.IndexOf("</script>", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (closeTagStart < 0) yield break;

            yield return html[searchFrom..closeTagStart];
            searchFrom = closeTagStart + "</script>".Length;
        }
    }

    private static bool IsJobPostingNode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("@type", out var typeEl)) return false;

        return typeEl.ValueKind switch
        {
            JsonValueKind.String => typeEl.GetString() == "JobPosting",
            JsonValueKind.Array => typeEl.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == "JobPosting"),
            _ => false,
        };
    }
}