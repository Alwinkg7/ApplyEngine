using System.Text.RegularExpressions;

namespace ApplyEngine.Domain.Processing;

/// <summary>
/// Best-effort, zero-cost (no LLM call) extraction of a "send your application here" email
/// address out of a job posting's raw text — spec §7 Channel A only ever applies to the subset
/// of postings that actually ask you to apply by emailing your resume directly, as opposed to
/// clicking through to a portal/form (that's Channel B, Phase 3, not this). Most postings from
/// LinkedIn/Indeed/HTML boards are portal-apply and simply won't have one — a null result here
/// is the expected, common case, not a failure.
///
/// Deliberately regex-only, same "free filter before the paid step" shape as KeywordFilter and
/// ResumeVariantSelector — there's no reason to spend an LLM call finding an email address.
/// Whatever this finds is always shown to you in the review queue before Send is ever clickable
/// (see Match.ApplyEmail's remarks) — this is a candidate, not a verified destination.
/// </summary>
public static class ApplyEmailExtractor
{
    private static readonly Regex EmailPattern = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Terms that, found near a candidate email, make it much more likely that email is actually
    // an apply-by-email address rather than some unrelated address mentioned in boilerplate
    // (a "contact us for partnerships" footer, a privacy-policy address, etc.).
    private static readonly string[] ApplyKeywords =
    {
        "apply", "send your resume", "send resume", "send cv", "email your resume",
        "share your resume", "share resume", "mail your resume", "mail us your resume",
        "careers@", "hr@", "recruitment", "recruiter", "hiring team", "walk-in",
    };

    // Address-shaped strings that are almost never a real apply-to destination even when they
    // technically match EmailPattern — automated senders, not a person or team who'd read a
    // resume. Filtered out only when there's no clearer apply-keyword-adjacent candidate to
    // prefer instead (see Extract below), not dropped outright, since a tiny company's only
    // published address might genuinely be something like "team@" that happens to also run
    // its notifications.
    private static readonly string[] LikelyNoiseTerms =
    {
        "noreply", "no-reply", "notifications", "notification", "donotreply", "do-not-reply",
        "mailer-daemon", "postmaster",
    };

    /// <summary>Returns the single best-guess apply email found in <paramref name="rawText"/>, or
    /// null if nothing plausible is there. Never throws on malformed/missing input.</summary>
    public static string? Extract(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        var candidates = EmailPattern.Matches(rawText)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // Multiple addresses in the text — prefer whichever one sits within a short window of an
        // apply-related keyword (e.g. "Send your resume to jobs@acme.com" — the keyword and the
        // address are almost always right next to each other in real postings).
        const int windowChars = 120;
        foreach (var email in candidates)
        {
            var idx = rawText.IndexOf(email, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var start = Math.Max(0, idx - windowChars);
            var length = Math.Min(rawText.Length, idx + email.Length + windowChars) - start;
            var window = rawText.Substring(start, length);

            if (ApplyKeywords.Any(k => window.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return email;
        }

        // No keyword-adjacent winner — fall back to the first candidate that doesn't look like an
        // automated sender address, or just the first candidate at all if every single one does.
        return candidates.FirstOrDefault(e => !LikelyNoiseTerms.Any(n => e.Contains(n, StringComparison.OrdinalIgnoreCase)))
            ?? candidates[0];
    }
}