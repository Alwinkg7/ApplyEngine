using ApplyEngine.Domain.Entities;

namespace ApplyEngine.Domain.Processing;

public record FilterResult(bool IsMatch, string Reason);

/// <summary>
/// The free, no-LLM hard filter from spec §3 ("Two-pass filter, not one" /
/// step 1). Cuts the obvious no's before anything reaches the paid fit-score
/// step that Phase 2 adds.
/// </summary>
public static class KeywordFilter
{
    public static FilterResult Evaluate(Job job, Preferences prefs)
    {
        // Message alerts ("you have a new message on LinkedIn") aren't job
        // postings — there's no title/stack/location to filter on, and the
        // whole point is that you see every one of them. Always pass.
        if (job.Kind == JobKind.MessageAlert)
            return new FilterResult(true, "Message alert — not filtered.");

        // Checked before anything else so a closed posting never shows up as
        // a match just because its title/stack/location happen to fit — see
        // JobEnrichmentParser for how IsClosed gets set.
        if (job.IsClosed)
            return new FilterResult(false, "Job posting is closed / no longer accepting applications.");

        if (prefs.ExcludeCompanies.Any(c => Contains(job.Company, c)))
            return new FilterResult(false, $"Company '{job.Company}' is on the exclude list.");

        if (prefs.MustHaveKeywords.Count > 0)
        {
            var haystack = $"{job.Title} {job.RawText}";
            var matchedKeyword = prefs.MustHaveKeywords.FirstOrDefault(k => Contains(haystack, k));
            if (matchedKeyword is null)
                return new FilterResult(false, "No must-have keyword found in title or description.");
        }

        if (prefs.Locations.Count > 0 && !MatchesAny(job, prefs.Locations, alwaysPassOn: "remote"))
            return new FilterResult(false, $"Location '{job.Location}' not in preferred list.");

        if (prefs.WorkModes.Count > 0 && !MatchesAny(job, prefs.WorkModes, alwaysPassOn: null))
            return new FilterResult(false, "No preferred work mode (e.g. Remote/Hybrid) found.");

        return new FilterResult(true, "Passed hard filter.");
    }

    /// <summary>
    /// Checks the structured Location field first, then falls back to the raw
    /// description text — most postings (RSS feeds especially) never give you
    /// a clean structured location, so relying on Location alone silently
    /// rejects almost everything from a source like that.
    ///
    /// For alert-mail-sourced jobs (ImapAlertFetcher), RawText used to be the
    /// ENTIRE alert email's text — meaning this fallback was really checking
    /// "does any job anywhere in that digest mention a preferred location",
    /// not "does this one". Fixed at the source: ImapAlertFetcher now gives
    /// each job link its own small windowed snippet of surrounding text
    /// instead of the whole email, so this fallback is meaningful per-job
    /// again. See ExtractJobSnippet's remarks there for the full story.
    /// </summary>
    private static bool MatchesAny(Job job, List<string> terms, string? alwaysPassOn)
    {
        var haystack = $"{job.Location} {job.RawText}";
        if (alwaysPassOn is not null && Contains(haystack, alwaysPassOn)) return true;
        return terms.Any(term => Contains(haystack, term));
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}