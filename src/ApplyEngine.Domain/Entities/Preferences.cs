namespace ApplyEngine.Domain.Entities;

/// <summary>Spec §3 "Qualification requirement": whether a degree/qualification mismatch found by
/// the LLM step should reject a job outright, or just be surfaced as a flag on the review card.
/// Default is Ignore — matches the spec's own example ("don't reject roles that ask for a degree
/// you don't hold, just flag it").</summary>
public enum QualificationRequirementMode
{
    Ignore = 0,
    Match = 1,
}

/// <summary>
/// Phase 0/1 covered enough of the full Preferences table (spec §3/§4) to run
/// the free hard filter. Phase 2 adds the fields the LLM fit-score step
/// (spec §3 "two-pass filter") needs: a salary floor, an experience band, a
/// minimum fit score, a qualification-mismatch policy, and a short profile
/// summary to score job descriptions against (a stand-in for the real master
/// resume until the resume engine — spec §5 — exists).
///
/// As of Phase 1 this is stored in the database instead of appsettings.json,
/// so the dashboard can edit it without a rebuild — see
/// ApplyEngine.Infrastructure.Data.PreferencesStore. It's a singleton row
/// (Id is always 1), not a multi-profile system.
/// </summary>
public class Preferences
{
    public int Id { get; set; } = 1;

    public int Version { get; set; } = 1;

    /// <summary>A job must match at least one of these (case-insensitive, substring) in title or raw text.</summary>
    public List<string> MustHaveKeywords { get; set; } = new();

    /// <summary>Scored but not currently used to reject in Phase 0 — kept for the Phase 2 fit-score step.</summary>
    public List<string> NiceToHaveKeywords { get; set; } = new();

    /// <summary>A job must mention at least one of these locations (in the structured Location field
    /// OR the raw description text — most postings never give you a clean structured field), OR the
    /// list is empty (no location filter). "Remote" always passes regardless of this list.</summary>
    public List<string> Locations { get; set; } = new();

    /// <summary>A job must mention at least one of these work arrangements (e.g. "Remote", "Hybrid",
    /// "Onsite"), matched the same way as Locations — structured field first, description text as
    /// fallback. Empty list = no work-mode filter (accept any arrangement).</summary>
    public List<string> WorkModes { get; set; } = new();

    /// <summary>Company names to always reject, regardless of fit (e.g. a past employer, a blacklisted recruiter).</summary>
    public List<string> ExcludeCompanies { get; set; } = new();

    /// <summary>Spec §3 "Salary floor" — a hard filter, but only when the LLM step actually finds a
    /// salary figure in the job text (most Indian listings don't state one). Null = no floor set;
    /// a job with no stated salary is never rejected on this basis either way.</summary>
    public decimal? SalaryFloorPerAnnum { get; set; }

    /// <summary>Spec §3 "Experience band" — informational bounds the LLM step compares its extracted
    /// experience requirement against; null on either end means that side of the band is unbounded.</summary>
    public int? ExperienceMinYears { get; set; }
    public int? ExperienceMaxYears { get; set; }

    /// <summary>Spec §3 "Qualification requirement" toggle — see QualificationRequirementMode.</summary>
    public QualificationRequirementMode QualificationRequirement { get; set; } = QualificationRequirementMode.Ignore;

    /// <summary>Spec §3 "Minimum fit score" slider (0-100). A job scoring below this is stored (see
    /// Match.Status) but never reaches the review queue.</summary>
    public int MinFitScore { get; set; } = 60;

    /// <summary>A few sentences describing your background, in your own words — years of experience,
    /// core stack, what kind of role you're after. This is what the LLM fit-score step compares each
    /// job description against (spec §3: "job description + your profile → 0-100 score"). Stands in
    /// for the real master resume until the resume engine (spec §5) exists; worth revisiting once it
    /// does, since the actual resume text is a richer signal than a hand-written summary.</summary>
    public string ProfileSummary { get; set; } = string.Empty;
}