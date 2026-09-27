namespace ApplyEngine.Domain.Entities;

/// <summary>
/// Distinguishes an actual job posting from a "someone messaged you"
/// notification pulled out of the same alert mailbox. Message alerts skip
/// the keyword/location/work-mode filter entirely (there's no job text to
/// match against) and are shown in their own digest section instead.
/// </summary>
public enum JobKind
{
    JobPosting = 0,
    MessageAlert = 1,
}

/// <summary>
/// A single job posting, normalized into one shape regardless of which
/// source it came from (RSS board, HTML board, or an alert-email link).
/// This mirrors the "Jobs" table in the production data model.
/// </summary>
public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public JobKind Kind { get; set; } = JobKind.JobPosting;

    public required string Title { get; set; }
    public required string Company { get; set; }
    public string? Location { get; set; }
    public string? ExperienceText { get; set; }
    public string? SalaryText { get; set; }
    public required string Url { get; set; }

    /// <summary>Full description text, kept so later re-scoring never needs to re-fetch the source.</summary>
    public string? RawText { get; set; }

    public required string SourceName { get; set; }

    /// <summary>SHA-256 of normalized company+title+url. Unique index in the DB — this is the dedupe key.</summary>
    public string DedupeHash { get; set; } = string.Empty;

    public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PostedAtUtc { get; set; }

    /// <summary>True if JobEnrichmentParser found evidence (on the posting's own page)
    /// that it's no longer accepting applications — e.g. a past schema.org validThrough
    /// date, or page text like "no longer accepting applications". Defaults to false
    /// (never guessed closed without evidence). Rejected by KeywordFilter regardless
    /// of how well the job otherwise matches.</summary>
    public bool IsClosed { get; set; } = false;

    /// <summary>When JobPageEnricher last attempted to fetch/parse this job's own posting
    /// page. Null means it's never been checked — either enrichment failed (network error,
    /// blocked request) or this job predates the enrichment feature.</summary>
    public DateTime? EnrichmentCheckedAtUtc { get; set; }
}