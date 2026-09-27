using System.Text.Json;
using System.Text.Json.Serialization;
using ApplyEngine.Domain.Entities;
using ApplyEngine.Domain.Processing;
using ApplyEngine.Infrastructure.Configuration;
using ApplyEngine.Infrastructure.Data;
using ApplyEngine.Infrastructure.Emails;
using ApplyEngine.Infrastructure.Fetching;
using ApplyEngine.Infrastructure.Notifications;
using ApplyEngine.Infrastructure.Pipeline;
using ApplyEngine.Infrastructure.Resumes;
using ApplyEngine.Infrastructure.Scoring;
using Microsoft.EntityFrameworkCore;
using Serilog;

// ---------------------------------------------------------------------------
// Phase 1 API: the dashboard's backend. Deliberately thin — it doesn't
// reimplement anything. Preferences read/write goes through PreferencesStore,
// and the pipeline trigger goes through JobPipelineService, both already
// proven working from Phase0's console runner. This is the backend for the
// real dashboard/ Next.js app (see dashboard/lib/api.ts) — CORS below exists
// specifically because that app runs on its own dev-server origin, not this
// one.
//
// Phase 2 adds the review-queue endpoints (GET /api/queue, GET
// /api/queue/{id}, POST .../approve|skip|requeue) alongside everything that
// was already here — /api/matches, /api/preferences, and /api/pipeline/run
// are all untouched, since the dashboard's existing Matches and Preferences
// pages already depend on them exactly as they were.
// ---------------------------------------------------------------------------

var logsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logsDirectory);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
    .WriteTo.Console(
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logsDirectory, "applyengine-api-.log"),
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// QuestPDF (Community License — free for personal/non-commercial use) powers
// ResumePdfRenderer, used below by both /api/queue/{id}/send (PDF attachment
// instead of the old plain-text one) and the new GET .../resume.pdf download
// endpoint. Must be set once before any Document.GeneratePdf() call anywhere
// in the process, so it goes here rather than near either individual call site.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() },
};
var configPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
var appConfig = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath), jsonOptions)
    ?? throw new InvalidOperationException("appsettings.json parsed to null.");
builder.Services.AddSingleton(appConfig);

// Same store ResumeTailor's caller (JobPipelineService) builds internally —
// needed here too so GET /api/queue/{id} can hand the dashboard the
// *original* base resume text alongside the tailored one, so the browser can
// render a diff between them (see that endpoint below).
builder.Services.AddSingleton(new ResumeVariantStore(appConfig.ResumeVariants));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(appConfig.ConnectionString, sql => sql.CommandTimeout(120)));

// Enums as their names ("Queued", not 1) in every JSON response — one less
// thing for the dashboard's TypeScript to get wrong, and readable directly in
// Swagger/a browser devtools tab.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// The Next.js dashboard runs on a different origin (its own dev server port)
// from this API — wide open for now since this is a single-user,
// localhost-only tool. Tighten this if it's ever exposed beyond your machine.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();

// Same EnsureCreated() caveat as always: only creates the DB from scratch,
// never alters an existing one. Nothing below adds a new column, so this is
// a no-op safety net against a genuinely fresh database, not a migration.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// ---- GET /api/preferences -------------------------------------------------
app.MapGet("/api/preferences", async (AppDbContext db) =>
{
    var prefs = await PreferencesStore.GetOrSeedAsync(db, appConfig.Preferences);
    return Results.Ok(prefs);
});

// ---- PUT /api/preferences -------------------------------------------------
// Explicit `Task<IResult>` return type here (and on every new handler below
// that can return more than one concrete Results.* type): NotFound() and
// Ok() are different C# types that both implement IResult, and minimal API
// route handlers need a single natural delegate type — without this
// annotation the lambda fails to compile with "the delegate type could not
// be inferred". Untouched otherwise from what was already here.
app.MapPut("/api/preferences", async Task<IResult> (Preferences updated, AppDbContext db) =>
{
    var existing = await db.Preferences.FirstOrDefaultAsync(p => p.Id == PreferencesStore.SingletonId);
    if (existing is null)
        return Results.NotFound("Preferences row doesn't exist yet — call GET /api/preferences once first to seed it.");

    existing.MustHaveKeywords = updated.MustHaveKeywords ?? new();
    existing.NiceToHaveKeywords = updated.NiceToHaveKeywords ?? new();
    existing.Locations = updated.Locations ?? new();
    existing.WorkModes = updated.WorkModes ?? new();
    existing.ExcludeCompanies = updated.ExcludeCompanies ?? new();
    existing.Version += 1;

    await db.SaveChangesAsync();
    return Results.Ok(existing);
});

// ---- GET /api/matches -------------------------------------------------
// Unchanged from before Phase 2 — still recomputed live against the free
// hard filter only (no FitScore/Matches-table awareness), still what the
// dashboard's existing Matches page (app/page.tsx) calls. The new /api/queue
// endpoints below are a separate, more capable view over the real Matches
// table (fit score, tailored resume, drafted email, approve/skip) — worth
// pointing the dashboard's nav at the Queue page day-to-day, but this one is
// left exactly as it was rather than folded together, so nothing here
// changes out from under app/page.tsx.
app.MapGet("/api/matches", async (AppDbContext db, int days = 14, int limit = 200) =>
{
    var cutoff = DateTime.UtcNow.AddDays(-days);
    var preferences = await PreferencesStore.GetOrSeedAsync(db, appConfig.Preferences);

    var candidates = await db.Jobs
        .Where(j => j.Kind == JobKind.JobPosting && j.FirstSeenAtUtc >= cutoff)
        .OrderByDescending(j => j.FirstSeenAtUtc)
        .Take(limit)
        .ToListAsync();

    var matches = candidates
        .Where(j => KeywordFilter.Evaluate(j, preferences).IsMatch)
        .Select(j => new
        {
            j.Id,
            j.Title,
            j.Company,
            j.Location,
            j.Url,
            j.SourceName,
            j.PostedAtUtc,
            j.FirstSeenAtUtc,
        });

    return Results.Ok(matches);
});

// ---- GET /api/queue --------------------------------------------------------
// The review queue's list view (new in Phase 2). Defaults to Queued (the
// ones actually waiting on you); pass ?status=Approved or ?status=Skipped to
// look back at a past decision. Sent is reachable the same way once Channel
// A exists, but nothing here ever sets or clears it (see the status-change
// endpoints below) — this queue only ever moves a match between
// Queued/Approved/Skipped.
app.MapGet("/api/queue", async Task<IResult> (AppDbContext db, string status = "Queued") =>
{
    if (!Enum.TryParse<MatchStatus>(status, ignoreCase: true, out var parsedStatus))
        return Results.BadRequest($"Unknown status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<MatchStatus>())}");

    var rows = await (
        from m in db.Matches
        join j in db.Jobs on m.JobId equals j.Id
        where m.Status == parsedStatus
        orderby m.FitScore descending, j.FirstSeenAtUtc descending
        select new
        {
            m.Id,
            j.Title,
            j.Company,
            j.Location,
            j.Url,
            j.SourceName,
            j.PostedAtUtc,
            j.FirstSeenAtUtc,
            m.FitScore,
            m.Reason,
            m.Status,
            m.ResumeVariant,
            HasTailoredResume = m.TailoredResumeMarkdown != null,
            HasEmailDraft = m.EmailBody != null,
            m.ApplyEmail,
        }
    ).ToListAsync();

    return Results.Ok(rows);
});

// ---- GET /api/queue/{id} ---------------------------------------------------
// Full detail for one match — everything the dashboard's detail page needs,
// including the *original* base resume text (looked up fresh from
// ResumeVariantStore by the variant name ResumeVariantSelector picked at
// tailoring time — see Match.ResumeVariant's remarks) so the browser can
// render a diff against TailoredResumeMarkdown without this endpoint having
// to do any diffing itself. Works regardless of the match's current status,
// so you can still open something you already approved or skipped.
app.MapGet("/api/queue/{id:guid}", async Task<IResult> (Guid id, AppDbContext db, ResumeVariantStore resumeVariantStore) =>
{
    var match = await db.Matches.FirstOrDefaultAsync(m => m.Id == id);
    if (match is null) return Results.NotFound();

    var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == match.JobId);
    if (job is null) return Results.Problem($"Match {id} references a Job ({match.JobId}) that no longer exists.", statusCode: 500);

    string? baseResumeMarkdown = null;
    if (!string.IsNullOrWhiteSpace(match.ResumeVariant) &&
        Enum.TryParse<ResumeVariantKind>(match.ResumeVariant, out var variantKind))
    {
        // Your current saved resume (dashboard's Resumes tab) if you've saved one for this
        // variant, otherwise the shipped default. Note this is the CURRENT base text, not
        // necessarily the exact text TailoredResumeMarkdown was tailored from — if you've
        // edited your base resume since this match was tailored, the diff below reflects
        // that edit too, not just what the LLM changed at tailoring time.
        baseResumeMarkdown = await ResumeVariantRepository.GetTextAsync(db, variantKind, resumeVariantStore);
    }

    return Results.Ok(new
    {
        match.Id,
        Job = new
        {
            job.Title,
            job.Company,
            job.Location,
            job.Url,
            job.SourceName,
            job.ExperienceText,
            job.SalaryText,
            job.PostedAtUtc,
            job.FirstSeenAtUtc,
        },
        match.FitScore,
        match.Reason,
        match.ExtractedSalary,
        match.ExtractedExperience,
        match.ExtractedQualification,
        match.Status,
        match.ResumeVariant,
        BaseResumeMarkdown = baseResumeMarkdown,
        match.TailoredResumeMarkdown,
        match.EmailSubject,
        match.EmailBody,
        match.ApplyEmail,
        match.ScoredAtUtc,
        match.ResumeTailoredAtUtc,
        match.EmailDraftedAtUtc,
        match.SentAtUtc,
    });
});

// ---- POST /api/queue/{id}/approve, /skip, /requeue -------------------------
// Three thin endpoints instead of one generic "PUT status" — each is a
// deliberate, named action a person takes from the dashboard (a button, not
// a dropdown), and it keeps the one real business rule (Sent is a one-way
// door) in a single shared helper rather than duplicated per endpoint.
app.MapPost("/api/queue/{id:guid}/approve", (Guid id, AppDbContext db) => SetMatchStatusAsync(id, db, MatchStatus.Approved));
app.MapPost("/api/queue/{id:guid}/skip", (Guid id, AppDbContext db) => SetMatchStatusAsync(id, db, MatchStatus.Skipped));
app.MapPost("/api/queue/{id:guid}/requeue", (Guid id, AppDbContext db) => SetMatchStatusAsync(id, db, MatchStatus.Queued));

// ---- POST /api/queue/{id}/send ---------------------------------------------
// Spec §7 Channel A: the one endpoint that actually sends anything. Deliberately
// its own explicit action rather than folded into Approve — Approve only ever
// changed a status; this hands your resume+draft to your SMTP server and can't
// be undone, so it earns its own button and its own set of guardrails:
//   - Only sendable once you've Approved it (never straight from Queued) —
//     one extra deliberate step before something irreversible happens.
//   - Refuses if ApplyEmailExtractor never found an address for this job — most
//     postings are portal-apply and legitimately have nothing to send to.
//   - Refuses if the tailored resume or drafted email never came together.
//   - Refuses (Conflict, same as the other three actions) if already Sent —
//     Sent is a one-way door, just like SetMatchStatusAsync already enforces.
app.MapPost("/api/queue/{id:guid}/send", async Task<IResult> (Guid id, AppDbContext db) =>
{
    var match = await db.Matches.FirstOrDefaultAsync(m => m.Id == id);
    if (match is null) return Results.NotFound();

    if (match.Status == MatchStatus.Sent)
        return Results.Conflict($"Match {id} has already been sent.");

    if (match.Status != MatchStatus.Approved)
        return Results.BadRequest("Only an Approved match can be sent — approve it from the review queue first.");

    if (string.IsNullOrWhiteSpace(match.ApplyEmail))
        return Results.BadRequest("No apply email was found for this job — this posting is most likely portal-apply, not email-apply, so there's nothing to send to.");

    if (string.IsNullOrWhiteSpace(match.EmailSubject) || string.IsNullOrWhiteSpace(match.EmailBody))
        return Results.BadRequest("This match has no drafted application email yet.");

    if (string.IsNullOrWhiteSpace(match.TailoredResumeMarkdown))
        return Results.BadRequest("This match has no tailored resume yet.");

    if (appConfig.Smtp is null)
        return Results.Problem("No Smtp section configured in appsettings.json — nothing to send with.", statusCode: 500);

    var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == match.JobId);
    var candidateFileNamePart = string.IsNullOrWhiteSpace(appConfig.CandidateName)
        ? "Resume"
        : appConfig.CandidateName.Replace(" ", "");
    var attachmentFileName = $"{candidateFileNamePart}_Resume_{(job?.Company ?? "Application").Replace(" ", "")}.pdf";

    byte[] resumePdfBytes;
    try
    {
        resumePdfBytes = ResumePdfRenderer.Render(match.TailoredResumeMarkdown!);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[Send] Match {MatchId} -> failed to render resume PDF", id);
        return Results.Problem($"Rendering the resume PDF failed: {ex.Message}", statusCode: 500);
    }

    var sender = new ApplicationEmailSender(appConfig.Smtp);
    try
    {
        await sender.SendAsync(
            match.ApplyEmail!,
            match.EmailSubject!,
            match.EmailBody!,
            attachmentFileName,
            resumePdfBytes);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[Send] Match {MatchId} -> failed to send via SMTP", id);
        return Results.Problem($"Sending failed: {ex.Message}", statusCode: 502);
    }

    match.Status = MatchStatus.Sent;
    match.SentAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();

    Log.Information("[Send] Match {MatchId} -> sent to {ApplyEmail}", id, match.ApplyEmail);
    return Results.Ok(new { match.Id, match.Status, match.SentAtUtc });
});

// ---- POST /api/queue/{id}/mark-applied --------------------------------------
// The manual counterpart to /send, for the far more common case: a posting
// with no apply email (see ApplyEmailExtractor's remarks — most real postings
// are portal-apply, not email-apply). Nothing here sends anything or knows
// whether you actually finished the employer's form; it just records that you
// did, the same way you'd tick off a paper checklist, so the match moves out
// of "Ready to Apply" the same way a real send moves a match out of Approved.
// Same one-way-door rule as every other status change: refuses once already
// Sent, and — like /send — only from Approved, so nothing gets marked applied
// before you've actually decided to apply.
app.MapPost("/api/queue/{id:guid}/mark-applied", async Task<IResult> (Guid id, AppDbContext db) =>
{
    var match = await db.Matches.FirstOrDefaultAsync(m => m.Id == id);
    if (match is null) return Results.NotFound();

    if (match.Status == MatchStatus.Sent)
        return Results.Conflict($"Match {id} is already marked as applied/sent.");

    if (match.Status != MatchStatus.Approved)
        return Results.BadRequest("Only an Approved match can be marked applied — approve it from the review queue first.");

    match.Status = MatchStatus.Sent;
    match.SentAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();

    Log.Information("[MarkApplied] Match {MatchId} -> marked applied (portal apply, no email sent by ApplyEngine)", id);
    return Results.Ok(new { match.Id, match.Status, match.SentAtUtc });
});

// ---- GET /api/queue/{id}/resume.pdf -----------------------------------------
// Lets the dashboard offer a real "Download PDF" link that returns the exact
// same server-rendered PDF Channel A would attach to an email (see
// ResumePdfRenderer's remarks), instead of the old workflow of opening the
// browser's print dialog and using Chrome's print-to-PDF against the on-page
// HTML preview. Read-only and side-effect-free — unlike /send and
// /mark-applied, this doesn't touch Status, so it works from any status and
// can be hit as many times as you like (e.g. to check formatting before
// deciding whether to send).
app.MapGet("/api/queue/{id:guid}/resume.pdf", async Task<IResult> (Guid id, AppDbContext db) =>
{
    var match = await db.Matches.FirstOrDefaultAsync(m => m.Id == id);
    if (match is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(match.TailoredResumeMarkdown))
        return Results.BadRequest("This match has no tailored resume yet.");

    var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == match.JobId);
    var candidateFileNamePart = string.IsNullOrWhiteSpace(appConfig.CandidateName)
        ? "Resume"
        : appConfig.CandidateName.Replace(" ", "");
    var downloadFileName = $"{candidateFileNamePart}_Resume_{(job?.Company ?? "Application").Replace(" ", "")}.pdf";

    byte[] pdfBytes;
    try
    {
        pdfBytes = ResumePdfRenderer.Render(match.TailoredResumeMarkdown!);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[ResumePdf] Match {MatchId} -> failed to render resume PDF", id);
        return Results.Problem($"Rendering the resume PDF failed: {ex.Message}", statusCode: 500);
    }

    return Results.File(pdfBytes, "application/pdf", downloadFileName);
});

// ---- GET /api/stats/applications --------------------------------------------
// Powers the dashboard's Applications tracker page — a simple read-only
// rollup over Matches with Status == Sent. Sent covers both real paths: an
// actual email via POST .../send (Channel A) and a manual POST
// .../mark-applied (the portal-apply path) — ApplyEmail tells the two apart
// after the fact (non-null means it was actually emailed), see its remarks
// on Match. Recomputed live on every request rather than cached: this is a
// personal, single-user tool with a small Matches table, so there's no real
// cost to just querying it fresh each time the page loads.
app.MapGet("/api/stats/applications", async (AppDbContext db) =>
{
    var sent = await (
        from m in db.Matches
        join j in db.Jobs on m.JobId equals j.Id
        where m.Status == MatchStatus.Sent && m.SentAtUtc != null
        select new { m.SentAtUtc, m.ApplyEmail, j.Company, j.SourceName }
    ).ToListAsync();

    var now = DateTime.UtcNow;
    var viaEmail = sent.Count(s => !string.IsNullOrWhiteSpace(s.ApplyEmail));

    var byCompany = sent
        .GroupBy(s => s.Company)
        .Select(g => new { Company = g.Key, Count = g.Count() })
        .OrderByDescending(g => g.Count)
        .ThenBy(g => g.Company)
        .Take(10)
        .ToList();

    var bySource = sent
        .GroupBy(s => s.SourceName)
        .Select(g => new { SourceName = g.Key, Count = g.Count() })
        .OrderByDescending(g => g.Count)
        .ThenBy(g => g.SourceName)
        .ToList();

    // 30-day daily timeline, zero-filled — a real chart needs every day
    // present (including days with nothing sent), or the bars would
    // silently misrepresent "zero that day" as "day didn't exist".
    var timelineStart = now.Date.AddDays(-29);
    var countsByDate = sent
        .GroupBy(s => s.SentAtUtc!.Value.Date)
        .ToDictionary(g => g.Key, g => g.Count());
    var dailyTimeline = Enumerable.Range(0, 30)
        .Select(offset => timelineStart.AddDays(offset))
        .Select(date => new { Date = date.ToString("yyyy-MM-dd"), Count = countsByDate.GetValueOrDefault(date) })
        .ToList();

    return Results.Ok(new
    {
        TotalSent = sent.Count,
        SentLast7Days = sent.Count(s => s.SentAtUtc >= now.AddDays(-7)),
        SentLast30Days = sent.Count(s => s.SentAtUtc >= now.AddDays(-30)),
        ViaEmail = viaEmail,
        ViaPortal = sent.Count - viaEmail,
        ByCompany = byCompany,
        BySource = bySource,
        DailyTimeline = dailyTimeline,
    });
});

// ---- GET /api/resumes, PUT /api/resumes/{kind} ------------------------------
// Powers the dashboard's Resumes tab: paste your own ATS-formatted resume for
// each variant (FullStack/Frontend/Backend) instead of hand-editing the .md
// files ResumeVariantStore reads by default (embedded, or an external path
// from appsettings.json) — see ResumeVariantRepository's remarks. A save here
// takes effect on the very next pipeline run; nothing needs restarting.
app.MapGet("/api/resumes", async (AppDbContext db, ResumeVariantStore resumeVariantStore) =>
{
    var variants = await ResumeVariantRepository.GetAllAsync(db, resumeVariantStore);
    return Results.Ok(variants);
});

app.MapPut("/api/resumes/{kind}", async Task<IResult> (
    string kind, ResumeUpdateRequest body, AppDbContext db, ResumeVariantStore resumeVariantStore) =>
{
    if (!Enum.TryParse<ResumeVariantKind>(kind, ignoreCase: true, out var parsedKind))
        return Results.BadRequest($"Unknown resume variant '{kind}'. Valid values: {string.Join(", ", Enum.GetNames<ResumeVariantKind>())}");

    if (string.IsNullOrWhiteSpace(body.Markdown))
        return Results.BadRequest("Resume text can't be empty.");

    var saved = await ResumeVariantRepository.UpsertAsync(db, parsedKind, body.Markdown);
    return Results.Ok(saved);
});

// ---- POST /api/pipeline/run -----------------------------------------------
// FIX (this pass): this endpoint was constructing JobPipelineService with
// only fitScorer + imapFetcher — never a ResumeTailor, ResumeVariantStore, or
// ApplicationEmailDrafter. That's why triggering a run from here (as opposed
// to Phase0's console runner, which always wired all three — see
// ApplyEngine.Phase0/Program.cs) fit-scored and queued matches just fine but
// silently never attempted resume tailoring or email drafting at all: inside
// JobPipelineService.RunAsync, `if (_resumeTailor is not null)` was false for
// every single match, so nothing under it ever ran, and the API host builds
// a *new* JobPipelineService per API call — this was never affected by
// which appsettings.json Llm section you have, only by this endpoint never
// having passed those three constructor args in the first place. Now built
// the same way Phase0/Program.cs does, from the same appConfig.Llm.
app.MapPost("/api/pipeline/run", async (AppDbContext db, ResumeVariantStore resumeVariantStore) =>
{
    using var http = new HttpClient();
    http.Timeout = TimeSpan.FromSeconds(30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("ApplyEngine-Api/0.1 (+personal job search tool)");

    await using var playwrightFetcher = new PlaywrightBoardFetcher(appConfig.JsRenderedBoards);
    await using var jobEnricher = new PlaywrightJobEnricher();
    using var llmHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var fitScorer = appConfig.Llm is not null ? new LlmFitScorer(llmHttp, appConfig.Llm) : null;
    var imapFetcher = appConfig.Imap is not null ? new ImapAlertFetcher(appConfig.Imap) : null;
    var resumeTailor = appConfig.Llm is not null ? new ResumeTailor(llmHttp, appConfig.Llm) : null;
    var emailDrafter = appConfig.Llm is not null ? new ApplicationEmailDrafter(llmHttp, appConfig.Llm, appConfig.CandidateName) : null;

    var pipeline = new JobPipelineService(
        db, appConfig, http, playwrightFetcher, jobEnricher, fitScorer, imapFetcher,
        resumeTailor, resumeVariantStore, emailDrafter);
    var result = await pipeline.RunAsync();

    return Results.Ok(result);
});

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

// Shared by the three status-change endpoints above.
static async Task<IResult> SetMatchStatusAsync(Guid id, AppDbContext db, MatchStatus newStatus)
{
    var match = await db.Matches.FirstOrDefaultAsync(m => m.Id == id);
    if (match is null) return Results.NotFound();

    // Sent is reserved for Channel A (the real send step, not built yet) —
    // once a match has actually been emailed, the review queue can't bounce
    // it back to Queued/Approved/Skipped as if it never happened.
    if (match.Status == MatchStatus.Sent)
        return Results.Conflict($"Match {id} has already been sent and can't be changed from the review queue.");

    match.Status = newStatus;
    await db.SaveChangesAsync();
    return Results.Ok(new { match.Id, match.Status });
}

// PUT /api/resumes/{kind}'s request body — just the pasted Markdown text.
// This (and any other real type declaration) MUST come after every local
// function above — CS8803: once a type declaration appears in a top-level-
// statements file, nothing that looks like a top-level statement (including
// a local function like SetMatchStatusAsync) is allowed after it. This was
// exactly the bug in the previous version of this file: the record was
// placed before SetMatchStatusAsync, which broke the build.
record ResumeUpdateRequest(string Markdown);