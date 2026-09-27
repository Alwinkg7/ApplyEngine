using ApplyEngine.Domain.Digest;
using ApplyEngine.Domain.Entities;
using ApplyEngine.Domain.Processing;
using ApplyEngine.Infrastructure.Configuration;
using ApplyEngine.Infrastructure.Data;
using ApplyEngine.Infrastructure.Emails;
using ApplyEngine.Infrastructure.Fetching;
using ApplyEngine.Infrastructure.Notifications;
using ApplyEngine.Infrastructure.Resumes;
using ApplyEngine.Infrastructure.Scoring;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace ApplyEngine.Infrastructure.Pipeline;

/// <summary>Summary of one pipeline run — returned so a caller (console output today,
/// a future GET /api/pipeline/status endpoint later) can report on it without re-deriving
/// anything from logs.</summary>
public class JobPipelineResult
{
    public int TotalFetched { get; set; }
    public int TotalNew { get; set; }
    public int MatchCount { get; set; }
    public bool DigestEmailed { get; set; }
    public string? DigestOutputPath { get; set; }
}

/// <summary>
/// Phase 0's six steps — collect, normalize, dedupe, hard-filter, digest, send
/// (spec §5) — extracted out of Phase0/Program.cs so a future host (the
/// Phase 1 API's POST /api/pipeline/run, most likely) can trigger the exact
/// same run on demand without duplicating this logic. Program.cs is now just
/// a composition root: build the dependencies below, call RunAsync() once,
/// log the result, exit. No behavior changed from the original Program.cs —
/// this is a pure extraction.
/// </summary>
public class JobPipelineService
{
    private readonly AppDbContext _db;
    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly PlaywrightBoardFetcher _playwrightFetcher;
    private readonly PlaywrightJobEnricher _jobEnricher;
    private readonly LlmFitScorer? _fitScorer;
    private readonly ImapAlertFetcher? _imapFetcher;
    private readonly ResumeTailor? _resumeTailor;
    private readonly ResumeVariantStore _resumeVariantStore;
    private readonly ApplicationEmailDrafter? _emailDrafter;

    // Randomized so a flat delay isn't itself a detectable pattern. Kept
    // short (0.5-1s) — see EnrichmentDelay comment below for why this isn't
    // tuned any slower than that.
    private static readonly Random _enrichmentDelayRng = new();

    public JobPipelineService(
        AppDbContext db,
        AppConfig config,
        HttpClient http,
        PlaywrightBoardFetcher playwrightFetcher,
        PlaywrightJobEnricher jobEnricher,
        LlmFitScorer? fitScorer,
        ImapAlertFetcher? imapFetcher,
        ResumeTailor? resumeTailor = null,
        ResumeVariantStore? resumeVariantStore = null,
        ApplicationEmailDrafter? emailDrafter = null)
    {
        _db = db;
        _config = config;
        _http = http;
        _playwrightFetcher = playwrightFetcher;
        _jobEnricher = jobEnricher;
        _fitScorer = fitScorer;
        _imapFetcher = imapFetcher;
        _resumeTailor = resumeTailor;
        // Always usable even with no resumeTailor configured (e.g. no Llm key) — the variant
        // *selection* (ResumeVariantSelector) is free either way; only the actual LLM tailoring
        // call is gated on _resumeTailor being non-null. Defaulting the config to null here just
        // means "use the built-in embedded resume text" — see ResumeVariantStore.
        _resumeVariantStore = resumeVariantStore ?? new ResumeVariantStore(config.ResumeVariants);
        _emailDrafter = emailDrafter;
    }

    public async Task<JobPipelineResult> RunAsync(CancellationToken ct = default)
    {
        // Loaded fresh on every call (not cached at construction) so a
        // dashboard edit to Preferences takes effect on the very next run,
        // including an on-demand run triggered from the future API.
        var preferences = await PreferencesStore.GetOrSeedAsync(_db, _config.Preferences, ct);

        ISourceFetcher[] fetchers =
        {
            new RssSourceFetcher(_http),
            new HtmlBoardFetcher(_http, _config.HtmlBoards),
            (ISourceFetcher?)_imapFetcher ?? NullFetcher.For(SourceType.ImapAlert),
            _playwrightFetcher,
        };

        var existingHashes = _db.Jobs.Select(j => j.DedupeHash).ToHashSet();
        var allNewJobs = new List<Job>();
        var totalFetched = 0;

        Log.Information("== ApplyEngine Phase 0 run — {RunStartedAtUtc:u} ==", DateTime.UtcNow);

        foreach (var sourceCfg in _config.Sources.Where(s => s.Enabled))
        {
            using var _scope = Serilog.Context.LogContext.PushProperty("Source", sourceCfg.Name);
            var source = await GetOrCreateSourceAsync(_db, sourceCfg);
            var fetcher = fetchers.FirstOrDefault(f => f.HandlesType == sourceCfg.Type);

            if (fetcher is null)
            {
                Log.Warning("[{Source}] SKIPPED — no fetcher registered for type {SourceType}", sourceCfg.Name, sourceCfg.Type);
                continue;
            }

            try
            {
                var rawJobs = await fetcher.FetchAsync(source);
                totalFetched += rawJobs.Count;
                Log.Debug("[{Source}] raw jobs before normalize/dedupe: {@RawJobs}",
                    sourceCfg.Name, rawJobs.Select(j => new { j.Title, j.Company, j.Url }));

                var normalized = rawJobs.Select(JobNormalizer.Normalize).ToList();

                var freshJobs = new List<Job>();
                foreach (var job in normalized)
                {
                    job.SourceName = sourceCfg.Name;
                    if (string.IsNullOrWhiteSpace(job.Location) && sourceCfg.DefaultLocation is not null)
                        job.Location = sourceCfg.DefaultLocation;
                    job.DedupeHash = DedupeService.ComputeHash(job);
                    if (existingHashes.Add(job.DedupeHash))
                        freshJobs.Add(job);
                }

                // Best-effort enrichment: load each new job's own posting page
                // in a real headless browser (PlaywrightJobEnricher) for its
                // real company name and open/closed status (see
                // JobEnrichmentParser). Only run on genuinely new jobs, never
                // on ones already in the DB — this bounds how much browser
                // traffic each run sends to LinkedIn/Indeed.
                //
                // LinkedIn: every job-view URL redirects anonymous requests
                // to its login wall (uas/login) regardless of pacing — that's
                // an authentication barrier, not something a slower request
                // rate gets around, confirmed across multiple real runs where
                // 100% of LinkedIn URLs hit the wall. Left as-is; no login is
                // wired into the browser context on purpose (using a real
                // account's session here risks LinkedIn flagging it for
                // automation). LinkedIn jobs keep showing "Unknown" — accepted.
                //
                // Indeed: initially looked like pacing-based rate-limiting (a
                // fast run saw 32/33 return 403, with the one success landing
                // after an incidental 22s gap), so the delay here was slowed
                // to 3-4.5s to test that theory. It didn't hold up — a second
                // real run at that slower pace still only got 1/35 through,
                // no better than the original fast pace (~2/68 successes
                // total across both runs, and even the "success" pages
                // sometimes have no parseable JobPosting data). So this isn't
                // a simple rate limit a slower request rate gets around; it's
                // treated the same as LinkedIn now — Indeed company names are
                // a rare bonus when a request happens to get through, not
                // something worth engineering for. Delay kept short (0.5-1s,
                // randomized) purely to avoid hammering the site pointlessly,
                // not as an anti-block measure.
                foreach (var job in freshJobs.Where(j => j.Kind == JobKind.JobPosting))
                {
                    var enrichment = await _jobEnricher.EnrichAsync(job.Url, ct);
                    if (enrichment is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(enrichment.Company))
                            job.Company = enrichment.Company!;
                        job.IsClosed = enrichment.IsClosed;
                    }
                    job.EnrichmentCheckedAtUtc = DateTime.UtcNow;

                    var delayMs = _enrichmentDelayRng.Next(500, 1000);
                    await Task.Delay(delayMs, ct);
                }

                _db.Jobs.AddRange(freshJobs);
                allNewJobs.AddRange(freshJobs);

                source.LastRunAtUtc = DateTime.UtcNow;
                source.LastStatus = "ok";
                source.LastJobCount = rawJobs.Count;

                Log.Information("[{Source}] fetched {FetchedCount}, {NewCount} new after dedupe",
                    sourceCfg.Name, rawJobs.Count, freshJobs.Count);
            }
            catch (Exception ex)
            {
                // Spec §2: "every layer degrades independently" — one bad source is
                // logged and skipped, never lets an exception kill the whole run.
                source.LastRunAtUtc = DateTime.UtcNow;
                source.LastStatus = $"error: {ex.Message}";
                Log.Error(ex, "[{Source}] FAILED", sourceCfg.Name);
            }
        }

        await _db.SaveChangesAsync(ct);

        // Only now — after the jobs are durably in the database — do we tell the
        // mailbox those emails are handled. If SaveChangesAsync above had thrown,
        // this line is simply never reached, so the emails stay unread and get
        // safely re-read (and deduped) next run instead of being silently lost.
        if (_imapFetcher is not null)
        {
            try
            {
                await _imapFetcher.ConfirmProcessedAsync();
            }
            catch (Exception ex)
            {
                // The jobs are already safely saved at this point — worst case
                // here is just re-reading the same emails next run (harmless).
                Log.Warning(ex, "Couldn't mark IMAP messages as processed after saving — they'll be safely re-read next run.");
            }
        }

        var matches = allNewJobs
            .Where(j => KeywordFilter.Evaluate(j, preferences).IsMatch)
            .ToList();

        Log.Information("-- {TotalNew} new jobs stored, {MatchCount} passed your preference filter --",
            allNewJobs.Count, matches.Count);
        Log.Debug("Matched jobs: {@Matches}",
            matches.Select(j => new { j.Title, j.Company, j.Location, j.Url, j.SourceName }));

        // Spec §3's paid second pass: LLM fit-score every match that's an
        // actual job posting (message alerts have no JD to score) and isn't
        // already scored against this Preferences version — the unique index
        // on (JobId, PreferencesVersion) would reject a duplicate anyway, but
        // checking first avoids a wasted OpenAI call. A job scoring below
        // preferences.MinFitScore is still stored (MatchStatus.New) so the
        // history exists for re-scoring later; only jobs that clear the bar
        // move to MatchStatus.Queued, which is what the future review-queue
        // endpoint will read from.
        //
        // Same "every layer degrades independently" rule as PlaywrightJobEnricher
        // and ImapAlertFetcher: no LLM configured, or a call failing, never
        // breaks the run — the job just gets an unscored Match row (FitScore
        // null) instead of one that blocks the pipeline.
        if (_fitScorer is null)
            Log.Debug("[FitScore] No LLM configured (AppConfig.Llm is null) — matches will be stored unscored.");

        // Fanout guard: a single alert-digest email (e.g. Indeed's "X. 21 more ...
        // jobs in ..." pattern) can link many postings that all share the exact
        // same title — they're the same underlying search result surfaced once
        // per company/link, not distinct roles. Scoring every one burns through
        // the LLM's tight free-tier rate budget for near-duplicate content (a
        // 22-link fanout turned a few-minute run into a 7-hour one). So only the
        // first MaxScoredPerTitleGroup postings per exact title are sent to the
        // scorer per run; the rest still get a Match row (so they're not lost —
        // they show up unscored, with a reason explaining why) but skip the LLM
        // call entirely. Grouping is by exact Title match only — deliberately
        // narrow, so it only catches genuine same-subject-line fanouts and never
        // touches two different jobs that happen to have similar titles.
        const int MaxScoredPerTitleGroup = 5;
        var titleScoreCounts = new Dictionary<string, int>();

        var jobPostingMatches = matches.Where(j => j.Kind == JobKind.JobPosting).ToList();
        foreach (var job in jobPostingMatches)
        {
            var alreadyScored = await _db.Matches.AnyAsync(
                m => m.JobId == job.Id && m.PreferencesVersion == preferences.Version, ct);
            if (alreadyScored) continue;

            var countSoFar = titleScoreCounts.GetValueOrDefault(job.Title);
            var skipFanout = countSoFar >= MaxScoredPerTitleGroup;

            FitScoreResult? score = null;
            string? skipReason = null;
            if (skipFanout)
            {
                skipReason = $"Skipped scoring — {countSoFar} other posting(s) with this exact title were already scored this run (fanout cap {MaxScoredPerTitleGroup}).";
                Log.Debug("[FitScore] {Title} @ {Company} -> {Reason}", job.Title, job.Company, skipReason);
            }
            else
            {
                titleScoreCounts[job.Title] = countSoFar + 1;
                if (_fitScorer is not null)
                {
                    score = await _fitScorer.ScoreAsync(job, preferences, ct);
                    await Task.Delay(300, ct); // light, polite pacing — no evidence the LLM endpoint needs it, unlike Indeed/LinkedIn
                }
            }

            var status = score?.FitScore is int s && s >= preferences.MinFitScore ? MatchStatus.Queued : MatchStatus.New;

            var newMatch = new Match
            {
                JobId = job.Id,
                PreferencesVersion = preferences.Version,
                FitScore = score?.FitScore,
                ExtractedSalary = score?.ExtractedSalary,
                ExtractedExperience = score?.ExtractedExperience,
                ExtractedQualification = score?.ExtractedQualification,
                Reason = score?.Reason ?? skipReason,
                Status = status,
                ScoredAtUtc = score is not null ? DateTime.UtcNow : null,
            };

            // Spec §5's resume tailoring engine: only worth running (and only worth spending a
            // second Groq call on) for matches that actually cleared the fit-score bar — tailoring
            // a resume for a job that scored below MinFitScore would just burn the same tight
            // rate-limit budget the fanout cap above exists to protect, for a job you likely
            // wouldn't apply to anyway. ResumeVariantSelector's pick is free (no LLM call) and
            // recorded even if the LLM tailoring call itself fails, so it's still visible which
            // variant *would* have been used.
            if (status == MatchStatus.Queued)
            {
                var variant = ResumeVariantSelector.Select(job);
                newMatch.ResumeVariant = variant.ToString();

                // Spec §7 Channel A: best-effort, no-LLM-call scan for a "send your resume here"
                // address in the posting's own text — see ApplyEmailExtractor's remarks. Null for
                // the (expected, common) case of a portal-apply posting with no email in it; the
                // review queue only ever offers the Send action once this is non-null AND you've
                // reviewed it there, never silently.
                newMatch.ApplyEmail = ApplyEmailExtractor.Extract(job.RawText);

                if (_resumeTailor is not null)
                {
                    // Your own saved resume (pasted through the dashboard's Resumes tab) always
                    // wins over the shipped embedded/external-file default — see
                    // ResumeVariantRepository's remarks. No setup required either way: this falls
                    // straight back to _resumeVariantStore until you've actually saved something.
                    var baseResume = await ResumeVariantRepository.GetTextAsync(_db, variant, _resumeVariantStore, ct);
                    var tailored = await _resumeTailor.TailorAsync(baseResume, job, ct);
                    await Task.Delay(300, ct); // same light pacing as the fit-score call above

                    // ResumeTailor.TailorAsync now treats an empty/whitespace-only model response
                    // as a failure (returns null) rather than a blank "success" — this check is
                    // just defense-in-depth against that same failure mode ever reappearing here
                    // (e.g. if TailorAsync's contract changes later), since a blank tailored resume
                    // stored on the match and handed to the email drafter would silently corrupt
                    // both.
                    if (!string.IsNullOrWhiteSpace(tailored))
                    {
                        newMatch.TailoredResumeMarkdown = tailored;
                        newMatch.ResumeTailoredAtUtc = DateTime.UtcNow;

                        // Spec §6's ATS email drafting: only attempted once a tailored resume
                        // actually exists to reference — an email drafted before tailoring
                        // succeeded would have nothing genuine to point to, and this is already
                        // the third LLM call for this one job, so it's gated the same way the
                        // second one was (only for jobs that made it this far).
                        if (_emailDrafter is not null)
                        {
                            var draft = await _emailDrafter.DraftAsync(job, tailored, ct);
                            await Task.Delay(300, ct); // same light pacing as the two calls above

                            if (draft is not null)
                            {
                                newMatch.EmailSubject = draft.Subject;
                                newMatch.EmailBody = draft.Body;
                                newMatch.EmailDraftedAtUtc = DateTime.UtcNow;
                            }
                        }
                    }
                }
            }

            _db.Matches.Add(newMatch);
        }

        if (jobPostingMatches.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            var queuedCount = await _db.Matches.CountAsync(
                m => m.PreferencesVersion == preferences.Version && m.Status == MatchStatus.Queued, ct);
            var tailoredCount = await _db.Matches.CountAsync(
                m => m.PreferencesVersion == preferences.Version && m.Status == MatchStatus.Queued && m.TailoredResumeMarkdown != null, ct);
            Log.Information("-- fit-scoring done — {QueuedTotal} match(es) at or above your minimum fit score of {MinFitScore} are now queued for review --",
                queuedCount, preferences.MinFitScore);
            if (_resumeTailor is not null)
                Log.Information("-- resume tailoring done — {TailoredCount}/{QueuedTotal} queued match(es) have a tailored resume ready --",
                    tailoredCount, queuedCount);
            if (_emailDrafter is not null)
            {
                var draftedCount = await _db.Matches.CountAsync(
                    m => m.PreferencesVersion == preferences.Version && m.Status == MatchStatus.Queued && m.EmailBody != null, ct);
                Log.Information("-- email drafting done — {DraftedCount}/{QueuedTotal} queued match(es) have a draft application email ready --",
                    draftedCount, queuedCount);
            }
        }

        var digestText = DigestBuilder.BuildPlainText(matches, DateTime.UtcNow);
        var subject = $"Job digest — {DateTime.UtcNow:yyyy-MM-dd} ({matches.Count} new match{(matches.Count == 1 ? "" : "es")})";

        var result = new JobPipelineResult
        {
            TotalFetched = totalFetched,
            TotalNew = allNewJobs.Count,
            MatchCount = matches.Count,
        };

        if (_config.DryRun || _config.Smtp is null)
        {
            var outPath = Path.Combine(AppContext.BaseDirectory, "digest-output.txt");
            await File.WriteAllTextAsync(outPath, digestText, ct);
            Log.Information("DryRun (or no SMTP configured) — digest written to {OutPath} instead of emailed.", outPath);
            result.DigestOutputPath = outPath;
        }
        else
        {
            var sender = new SmtpDigestSender(_config.Smtp);
            await sender.SendAsync(subject, digestText);
            Log.Information("Digest emailed.");
            result.DigestEmailed = true;
        }

        return result;
    }

    private static async Task<Source> GetOrCreateSourceAsync(AppDbContext db, SourceConfig cfg)
    {
        var existing = await db.Sources.FirstOrDefaultAsync(s => s.Name == cfg.Name);
        if (existing is not null) return existing;

        var created = new Source { Name = cfg.Name, Type = cfg.Type, Endpoint = cfg.Endpoint, Enabled = cfg.Enabled };
        db.Sources.Add(created);
        return created;
    }
}

/// <summary>Stand-in fetcher for a source type that isn't configured (e.g. no IMAP credentials yet) — fails loud and specific instead of null-refing.</summary>
file class NullFetcher : ISourceFetcher
{
    public required SourceType HandlesType { get; init; }
    public Task<List<Job>> FetchAsync(Source source, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Source '{source.Name}' is type {HandlesType} but no configuration was provided for it (e.g. Imap section missing in appsettings.json).");

    public static NullFetcher For(SourceType type) => new() { HandlesType = type };
}