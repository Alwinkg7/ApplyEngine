using ApplyEngine.Domain.Entities;

namespace ApplyEngine.Domain.Processing;

/// <summary>Which of the three pre-written resume variants best matches a job posting's
/// emphasis. Deliberately just three buckets, matching the three resumes the candidate
/// actually maintains — see ResumeVariantStore in Infrastructure for where the underlying
/// text lives.</summary>
public enum ResumeVariantKind
{
    FullStack,
    Frontend,
    Backend,
}

/// <summary>
/// Zero-cost (no LLM call) first pass for resume tailoring: picks which of the three
/// existing resume variants is the closest starting point for a given job posting, purely
/// by counting frontend- vs backend-signal keywords in the title + description. This mirrors
/// KeywordFilter's "free filter before the paid step" approach (spec §3) — the paid LLM step
/// (ResumeTailor, Infrastructure) only has to lightly tailor an already-close-enough resume,
/// not decide from scratch which one to use, which would cost extra tokens on an already
/// tight rate budget for no real accuracy gain (three buckets don't need an LLM to pick).
/// </summary>
public static class ResumeVariantSelector
{
    // Deliberately short, high-signal lists — a handful of terms that reliably distinguish
    // "this posting wants mostly UI work" from "this posting wants mostly server/data work"
    // without needing to be exhaustive. Ties (or a full-stack-flavored posting matching both
    // sides roughly equally) fall through to FullStack, which is written to read fine either way.
    private static readonly string[] FrontendSignals =
    {
        "frontend", "front-end", "front end", "react", "next.js", "nextjs", "ui developer",
        "ui/ux", "css", "tailwind", "javascript developer", "typescript developer",
        "web designer", "vue", "angular",
    };

    private static readonly string[] BackendSignals =
    {
        "backend", "back-end", "back end", "api developer", "server-side", "sql server",
        "database developer", "microservices", "node.js developer", ".net developer",
        "asp.net", "devops", "cloud engineer", "system design",
    };

    public static ResumeVariantKind Select(Job job)
    {
        var haystack = $"{job.Title} {job.RawText}";

        var frontendScore = FrontendSignals.Count(term => Contains(haystack, term));
        var backendScore = BackendSignals.Count(term => Contains(haystack, term));

        if (frontendScore > backendScore) return ResumeVariantKind.Frontend;
        if (backendScore > frontendScore) return ResumeVariantKind.Backend;
        return ResumeVariantKind.FullStack;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}