namespace ApplyEngine.Domain.Entities;

/// <summary>
/// Spec §4 Matches.status. "New" is a scored job that didn't clear
/// Preferences.MinFitScore — stored for history/re-scoring, but never shown
/// in the review queue. "Queued" is a scored job that did clear the bar and
/// is waiting for you. Approved/Skipped/Sent arrive with the review-queue
/// and resume/email/send work later in Phase 2.
/// </summary>
public enum MatchStatus
{
    New = 0,
    Queued = 1,
    Approved = 2,
    Skipped = 3,
    Sent = 4,
}

/// <summary>
/// One row per (Job, Preferences.Version) pair — spec §4 Matches table.
/// Created only for jobs that already cleared KeywordFilter's free hard
/// filter (spec §3 "filter first, generate second": nothing reaches the
/// paid LLM step until it's passed the free one). Tagging every row with
/// the Preferences version it was scored against means tightening your
/// filters later doesn't retroactively corrupt history — see spec §3.
/// </summary>
public class Match
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }

    /// <summary>Which Preferences.Version this job was scored against — see class remarks.</summary>
    public int PreferencesVersion { get; set; }

    /// <summary>0-100, from IFitScorer. Null means scoring was attempted but failed/was skipped
    /// (e.g. no LLM configured, or the API call errored) — the job still passed the hard filter,
    /// it's just unscored. Kept distinct from "scored 0" so a real bad fit isn't confused with
    /// "we never actually asked the LLM".</summary>
    public int? FitScore { get; set; }

    /// <summary>Salary figure the LLM found in the job text, in whatever form it was written (not
    /// normalized to a number) — e.g. "₹8-12 LPA". Null if the JD didn't mention one.</summary>
    public string? ExtractedSalary { get; set; }

    /// <summary>Years-of-experience requirement the LLM found in the job text (e.g. "3-5 years").</summary>
    public string? ExtractedExperience { get; set; }

    /// <summary>Qualification requirement the LLM found (e.g. "B.Tech required") — informational
    /// only for now; spec §3's "match / ignore mismatch" toggle doesn't reject on this yet.</summary>
    public string? ExtractedQualification { get; set; }

    /// <summary>One-line reason from the LLM — why this score, in plain language. Shown on the
    /// review card later; also the fastest way to sanity-check the scorer isn't hallucinating.</summary>
    public string? Reason { get; set; }

    public MatchStatus Status { get; set; } = MatchStatus.New;

    public DateTime? ScoredAtUtc { get; set; }

    /// <summary>Which of the three resume variants (FullStack/Frontend/Backend — see
    /// ResumeVariantSelector) was picked as the tailoring starting point for this match.
    /// Stored as a string, not the enum, purely so a future variant can be added without an
    /// enum-to-int renumbering headache. Null until tailoring has actually run for this match
    /// (see JobPipelineService — only Queued matches get tailored, not every scored one).</summary>
    public string? ResumeVariant { get; set; }

    /// <summary>The LLM-tailored resume text (Markdown) for this specific job, or null if
    /// tailoring hasn't run yet / wasn't configured / the call failed — same "degrades
    /// independently" treatment as FitScore being null. Review-queue UI (later) reads this
    /// directly; nothing here is regenerated automatically once set.</summary>
    public string? TailoredResumeMarkdown { get; set; }

    public DateTime? ResumeTailoredAtUtc { get; set; }

    /// <summary>Spec §6's drafted application email subject line, or null if drafting hasn't run
    /// yet / wasn't configured / the call failed — same "degrades independently" treatment as
    /// TailoredResumeMarkdown. Only ever populated alongside EmailBody (see
    /// ApplicationEmailDrafter / JobPipelineService) — only attempted once a tailored resume
    /// already exists for this match, since the email references it.</summary>
    public string? EmailSubject { get; set; }

    /// <summary>The drafted application email body (plain text) — references the tailored resume
    /// as "attached" but nothing is actually attached yet; real attachment/sending is spec §7's
    /// Channel A, a separate later piece. This is draft-only: nothing here touches a mailbox.</summary>
    public string? EmailBody { get; set; }

    public DateTime? EmailDraftedAtUtc { get; set; }

    /// <summary>Spec §7 Channel A: the address JobPipelineService found for this match (via
    /// ApplyEmailExtractor scanning Job.RawText for something that looks like "email your resume
    /// to X" or a careers@/hr@ address) — or null if no plausible address was found. Most postings
    /// point to an application URL, not a raw email address, so this is expected to be null far
    /// more often than not; it's only ever populated for Queued matches, same gating as
    /// ResumeVariant/TailoredResumeMarkdown. Best-effort and unverified — always shown to you in
    /// the review queue before Send is ever clicked, never sent to blind.</summary>
    public string? ApplyEmail { get; set; }

    /// <summary>When POST /api/queue/{id}/send actually succeeded in handing the message to your
    /// SMTP server — the one moment Status flips to Sent. Null until then.</summary>
    public DateTime? SentAtUtc { get; set; }
}