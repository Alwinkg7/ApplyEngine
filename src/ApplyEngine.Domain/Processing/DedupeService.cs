using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApplyEngine.Domain.Entities;

namespace ApplyEngine.Domain.Processing;

/// <summary>
/// Computes the dedupe hash described in spec §4 (Jobs.dedupeHash).
///
/// Originally this hashed normalized company + title + link. That broke badly
/// for IMAP alert-email jobs (see ImapAlertFetcher.ExtractJobsFromAlertEmail):
/// Phase 0 doesn't parse a real per-job title/company out of the alert HTML
/// (that's deferred to Phase 2's LLM step) — every job pulled from one email
/// gets Company = the literal constant "Unknown — see link" and Title = that
/// whole email's Subject line. So the *same* job-view link, seen again in a
/// *different* alert email (a very common case — LinkedIn/Indeed re-send the
/// same posting across several digests), got a different Subject each time
/// and hashed as a brand-new job. A real run showed this as "447 fetched, 447
/// new after dedupe" — literally zero of 447 recognized as duplicates.
///
/// The link is the one piece of a Phase-0 job that's always real and always
/// identifies one specific posting, regardless of source, so it's now the
/// entire key. Company/Title only get folded in as a defensive fallback for
/// a job that somehow has no link at all.
/// </summary>
public static class DedupeService
{
    private static readonly Regex IndeedJobIdParam = new(@"[?&]jk=([^&]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string ComputeHash(Job job)
    {
        var urlKey = NormalizeUrlForKey(job.Url);
        var key = string.IsNullOrWhiteSpace(urlKey)
            ? string.Join('|', NormalizeKeyPart(job.Company), NormalizeKeyPart(job.Title))
            : urlKey;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeKeyPart(string value) =>
        value.Trim().ToLowerInvariant();

    private static string NormalizeUrlForKey(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var lowered = url.Trim().ToLowerInvariant();

        // Indeed alert links are click-tracking redirects
        // ("in.indeed.com/rc/clk/dl?jk=...&from=ja&qd=...&rd=...&tk=...&alid=...
        // &bb=...&tmtk=..."). The redirect host/path and most params vary
        // per-email even for the exact same posting; only "jk=" (Indeed's job
        // id) is stable. Rather than chase each new tracking param Indeed adds,
        // collapse any indeed.com link straight to its job id.
        var jkMatch = IndeedJobIdParam.Match(lowered);
        if (jkMatch.Success && lowered.Contains("indeed.com"))
            return $"indeed:jk={jkMatch.Groups[1].Value}";

        // Tracking params are already stripped by JobNormalizer before this runs,
        // but defensively strip again here so the hash is stable even if a caller
        // forgets to normalize first.
        var stripped = JobNormalizer.StripTrackingParams(lowered);
        return stripped.TrimEnd('/');
    }
}