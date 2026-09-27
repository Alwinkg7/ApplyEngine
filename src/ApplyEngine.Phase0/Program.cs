using System.Text.Json;
using System.Text.Json.Serialization;
using ApplyEngine.Infrastructure.Configuration;
using ApplyEngine.Infrastructure.Data;
using ApplyEngine.Infrastructure.Emails;
using ApplyEngine.Infrastructure.Fetching;
using ApplyEngine.Infrastructure.Pipeline;
using ApplyEngine.Infrastructure.Resumes;
using ApplyEngine.Infrastructure.Scoring;
using Microsoft.EntityFrameworkCore;
using Serilog;

// ---------------------------------------------------------------------------
// Phase 0 runner: "console app + scheduled task" (spec §5). Run this once a
// day via cron / Windows Task Scheduler / Hangfire-later.
//
// As of Phase 1, this file is only a composition root: it builds the
// dependencies (config, DbContext, HttpClient, fetchers) and hands them to
// JobPipelineService.RunAsync(), which does the actual six steps (collect,
// normalize, dedupe, hard-filter, digest, send). That extraction means the
// Phase 1 API (added later) can trigger the exact same run — e.g. from a
// POST /api/pipeline/run endpoint — without duplicating any of this logic.
// No UI, no LLM, no apply here — that's Phase 1+.
// ---------------------------------------------------------------------------

// Logs land in a "logs" folder next to the project (relative to the current
// working directory, which `dotnet run` sets to the project folder — same
// assumption the config-path lookup below already relies on). One file per
// day, kept for 14 days, so a bad run six days ago is still inspectable.
var logsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logsDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug() // global floor; each sink below narrows it further
    .Enrich.FromLogContext()
    .WriteTo.Console(
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information, // terminal stays readable
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logsDirectory, "applyengine-.log"),
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug, // file gets full per-job data
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var configPath = args.Length > 0 ? args[0] : "appsettings.json";
    if (!File.Exists(configPath))
    {
        var examplePath = Path.Combine(AppContext.BaseDirectory, "appsettings.Example.json");
        Log.Warning("No config at {ConfigPath}. Copy appsettings.Example.json to appsettings.json next to it, fill in your sources/credentials, and re-run. Example file: {ExamplePath}",
            configPath, examplePath);
        return 1;
    }

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath), jsonOptions)
                 ?? throw new InvalidOperationException("Config file parsed to null.");

    using var db = new AppDbContext(
        new DbContextOptionsBuilder<AppDbContext>()
            // Default SQL command timeout (30s) was too tight once a real
            // inbox started producing 100+ new jobs in one save — raised to
            // give a slower local SQL Server instance real headroom. If a
            // save still times out at 120s, that's blocking/locking, not row
            // count — check for an open transaction elsewhere (e.g. a stray
            // SSMS window) rather than raising this further.
            .UseSqlServer(config.ConnectionString, sql => sql.CommandTimeout(120))
            .Options);
    db.Database.EnsureCreated();

    using var http = new HttpClient();
    http.Timeout = TimeSpan.FromSeconds(30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("ApplyEngine-Phase0/0.1 (+personal job search tool)");

    await using var playwrightFetcher = new PlaywrightBoardFetcher(config.JsRenderedBoards);
    await using var jobEnricher = new PlaywrightJobEnricher();

    // Separate HttpClient from the one used for board fetching — LLM calls
    // are slower than a board fetch and shouldn't share that timeout budget.
    // Null when no Llm config is present (see AppConfig.Llm) — the pipeline
    // treats that the same as no Imap/Smtp config: skip, don't fail.
    using var llmHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var fitScorer = config.Llm is not null ? new LlmFitScorer(llmHttp, config.Llm) : null;

    // Spec §5's resume tailoring — reuses the same Llm config/endpoint as fit-scoring (no
    // second API key needed) and is null under the exact same condition (no Llm configured),
    // same "degrades independently" treatment. ResumeVariantStore has no config dependency of
    // its own to construct here — JobPipelineService builds one internally from
    // config.ResumeVariants if one isn't passed in.
    var resumeTailor = config.Llm is not null ? new ResumeTailor(llmHttp, config.Llm) : null;

    // Spec §6's ATS email drafting — same "reuse the Llm config, null under the same condition"
    // treatment as resumeTailor above. CandidateName is optional (see AppConfig remarks); passing
    // it through just controls the sign-off on drafted emails, nothing else.
    var emailDrafter = config.Llm is not null ? new ApplicationEmailDrafter(llmHttp, config.Llm, config.CandidateName) : null;

    // Held separately (not just inside the pipeline) — same reasoning as
    // before: it's disposed here, at the composition root, alongside http,
    // playwrightFetcher, and jobEnricher.
    var imapFetcher = config.Imap is not null ? new ImapAlertFetcher(config.Imap) : null;

    var pipeline = new JobPipelineService(db, config, http, playwrightFetcher, jobEnricher, fitScorer, imapFetcher, resumeTailor, emailDrafter: emailDrafter);
    var result = await pipeline.RunAsync();

    Log.Information("Run complete — {TotalFetched} fetched, {TotalNew} new, {MatchCount} matched.",
        result.TotalFetched, result.TotalNew, result.MatchCount);

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Phase 0 run crashed");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}