using System.Net;
using System.Text.RegularExpressions;
using ApplyEngine.Domain.Entities;

namespace ApplyEngine.Domain.Processing;

/// <summary>
/// Cleans a freshly-parsed job into the one shape every downstream stage
/// expects (spec §2, "Normalize + dedupe"): trimmed whitespace, decoded
/// HTML entities, stripped tracking parameters on the link.
/// </summary>
public static class JobNormalizer
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);

    public static Job Normalize(Job job)
    {
        job.Title = CleanText(job.Title);
        job.Company = CleanText(job.Company);
        job.Location = job.Location is null ? null : CleanText(job.Location);
        job.RawText = job.RawText is null ? null : CleanText(StripHtml(job.RawText));
        // Href values come straight out of raw alert-email HTML via regex (see
        // ImapAlertFetcher.ExtractJobsFromAlertEmail), so their query-string
        // separators are still the literal HTML entity "&amp;", not "&". A real
        // run showed this defeating StripTrackingParams almost entirely: splitting
        // "...?trackingId=X&amp;refId=Y&amp;trk=Z" on '&' produces "trackingId=X",
        // "amp;refId=Y", "amp;trk=Z" — everything after the first param comes out
        // with an "amp;" prefix glued onto its name, so it never matches the
        // exact-name checks below ("refid", "trk", ...) and survives unstripped.
        // Only the very first param (with no leading "&amp;") was ever actually
        // removed. Decoding entities first — same as Title/Company/RawText above —
        // turns "&amp;" back into a real "&" so every param splits and strips
        // correctly.
        job.Url = StripTrackingParams(WebUtility.HtmlDecode(job.Url).Trim());
        return job;
    }

    private static string CleanText(string input)
    {
        var decoded = WebUtility.HtmlDecode(input);
        return WhitespaceRun.Replace(decoded, " ").Trim();
    }

    private static string StripHtml(string input) => HtmlTag.Replace(input, " ");

    /// <summary>
    /// Drops query-string tracking params (utm_*, ref, src, trackingId, ...) so the
    /// same posting reached via two different tracked links still dedupes to one job.
    ///
    /// The LinkedIn-specific params below (trkEmail, eid, midToken, midSig,
    /// otpToken, lipi) were added after a real alert email showed the *same*
    /// job-view link appearing 3-4 times per posting — once per element it was
    /// attached to (header, company logo, job card body, ...) — each copy
    /// differing only in trkEmail (e.g. "...-company_logo_0_jobid_123..." vs
    /// "...-job_posting_0_jobid_123..."). With "trk" alone stripped but
    /// "trkEmail" surviving, every copy hashed differently and dedupe never
    /// collapsed them, so one real posting turned into 3-4 stored "jobs".
    /// </summary>
    public static string StripTrackingParams(string url)
    {
        var qIndex = url.IndexOf('?');
        if (qIndex < 0) return url;

        var basePart = url[..qIndex];
        var query = url[(qIndex + 1)..];

        var keptParams = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p =>
            {
                var key = p.Split('=')[0].ToLowerInvariant();
                return !(key.StartsWith("utm_") || key is "ref" or "src" or "trackingid" or "refid" or "trk"
                    or "trkemail" or "eid" or "midtoken" or "midsig" or "otptoken" or "lipi");
            })
            .ToList();

        return keptParams.Count == 0 ? basePart : $"{basePart}?{string.Join('&', keptParams)}";
    }
}