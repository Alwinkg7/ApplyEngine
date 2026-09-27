using ApplyEngine.Domain.Entities;
using ApplyEngine.Infrastructure.Fetching;
using ApplyEngine.Infrastructure.Notifications;
using ApplyEngine.Infrastructure.Scoring;

namespace ApplyEngine.Infrastructure.Configuration;

/// <summary>Optional override paths for the three resume variants — leave any (or all) of
/// these null to use the built-in copy shipped as an embedded resource (see
/// ResumeVariantStore). Point one at a real file on disk once you want to edit your resume's
/// wording without rebuilding the app — a plain .md/.txt file, read fresh on every run.</summary>
public class ResumeVariantsConfig
{
    public string? FullStackPath { get; set; }
    public string? FrontendPath { get; set; }
    public string? BackendPath { get; set; }
}

/// <summary>
/// Moved here from ApplyEngine.Phase0 so both the Phase 0 console app and the
/// Phase 1 API (added later) can deserialize the same appsettings.json shape
/// without the API project having to reference the console app project.
/// </summary>
public class SourceConfig
{
    public required string Name { get; set; }
    public required SourceType Type { get; set; }
    public required string Endpoint { get; set; }
    public bool Enabled { get; set; } = true;
    public string? DefaultLocation { get; set; }
}

public class AppConfig
{
    /// <summary>If true, no email is sent and no live send credentials are required — the
    /// digest is written to digest-output.txt instead. Start here; flip off once SMTP works.</summary>
    public bool DryRun { get; set; } = false;

    public required string ConnectionString { get; set; }

    public List<SourceConfig> Sources { get; set; } = new();
    public Dictionary<string, BoardSelectorConfig> HtmlBoards { get; set; } = new();
    public Dictionary<string, PlaywrightBoardSelectorConfig> JsRenderedBoards { get; set; } = new();
    public Preferences Preferences { get; set; } = new();

    public ImapOptions? Imap { get; set; }
    public SmtpOptions? Smtp { get; set; }

    /// <summary>Null = LLM fit scoring is skipped entirely — matches from the free hard filter are
    /// still stored, just with FitScore null (see JobPipelineService). Same "degrades independently"
    /// treatment as Imap/Smtp being optional: no LLM key configured shouldn't stop the rest of the
    /// pipeline from running. See LlmOptions remarks — this isn't tied to any one provider (defaults
    /// to Groq's free tier, but OpenAI/Gemini/a local Ollama all work by changing Endpoint+Model).</summary>
    public LlmOptions? Llm { get; set; }

    /// <summary>Optional override paths for the three resume variants — null (the default)
    /// means "use the built-in embedded copies as-is". Resume tailoring itself (see
    /// ResumeTailor / JobPipelineService) reuses the Llm config above rather than needing a
    /// second API key — if Llm is null, resume tailoring is skipped the same way fit-scoring
    /// is skipped, no separate config required.</summary>
    public ResumeVariantsConfig? ResumeVariants { get; set; }

    /// <summary>Your name, used only as the sign-off on drafted application emails (spec §6 — see
    /// ApplicationEmailDrafter). Optional: leave it out and drafts sign off with a "[Your Name]"
    /// placeholder for you to fill in by hand instead.</summary>
    public string? CandidateName { get; set; }
}