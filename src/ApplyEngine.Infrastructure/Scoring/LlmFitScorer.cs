using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyEngine.Domain.Entities;
using Serilog;

namespace ApplyEngine.Infrastructure.Scoring;

/// <summary>
/// Provider-agnostic on purpose: OpenAI, Groq, Google's Gemini OpenAI-compat
/// endpoint, and a locally-running Ollama all speak the same "chat
/// completions" request/response shape, so LlmFitScorer never needs to know
/// which one it's actually talking to — only Endpoint/Model/ApiKey change.
/// Defaults point at Groq's free tier (console.groq.com) rather than OpenAI:
/// same wire format, no per-token charge within its rate limits. Swap
/// Endpoint+Model to point at OpenAI, Gemini's OpenAI-compat endpoint
/// (generativelanguage.googleapis.com/v1beta/openai/), or a local Ollama
/// (typically http://localhost:11434/v1/chat/completions, ApiKey can be any
/// non-empty placeholder string since Ollama doesn't check it) instead.
/// </summary>
public class LlmOptions
{
    /// <summary>For Ollama specifically, this can be any non-empty placeholder — Ollama's
    /// OpenAI-compat endpoint doesn't actually check it, but the Authorization header still
    /// needs a value to send.</summary>
    public required string ApiKey { get; set; }

    public string Endpoint { get; set; } = "https://api.groq.com/openai/v1/chat/completions";

    /// <summary>A model your chosen Endpoint actually serves — these are NOT interchangeable
    /// across providers (a Groq model name means nothing to OpenAI's API, and vice versa).
    /// Default is Groq's current free-tier chat model. Groq moved the Llama 3.1/3.3 models to
    /// Enterprise-only at some point after this was first written (a free-tier key gets a 404
    /// "model ... does not exist or you do not have access to it" for llama-3.3-70b-versatile
    /// now) — check console.groq.com/docs/models if this one has been retired too by the time
    /// you read this; hosted free-model lineups change over time.</summary>
    public string Model { get; set; } = "openai/gpt-oss-20b";
}

/// <summary>Spec §3 "LLM fit score" step's output — one row's worth of Match fields. Null FitScore
/// means the call itself failed (network error, bad response, etc.), not that the job scored zero.</summary>
public record FitScoreResult(
    int? FitScore,
    string? ExtractedSalary,
    string? ExtractedExperience,
    string? ExtractedQualification,
    string? Reason);

/// <summary>
/// Spec §3's paid second pass: job description + candidate profile → 0-100
/// score, extracted salary/experience/qualification, and a one-line reason.
/// Only ever called on jobs that already cleared KeywordFilter's free hard
/// filter — see JobPipelineService — so cost (on a provider that has one)
/// is bounded by design, not just by budget discipline.
///
/// Same "every layer degrades independently" rule as every other external
/// call in this codebase (PlaywrightJobEnricher, ImapAlertFetcher): a
/// timeout, a malformed response, or a missing API key all result in a null
/// FitScore rather than an exception that could take down a pipeline run.
/// Raw HttpClient + System.Text.Json rather than a provider SDK package or
/// System.Net.Http.Json — keeps this to exactly the two BCL-level pieces
/// already used elsewhere in this project, and (deliberately) means it isn't
/// tied to any one provider's SDK either — see LlmOptions remarks.
/// </summary>
public class LlmFitScorer
{
    // Lowered from an original 12,000 after real runs showed a specific, recurring failure
    // mode: for AlertMailbox-sourced jobs, RawText wasn't that one posting's own description —
    // ImapAlertFetcher used to set it to the WHOLE digest email's TextBody, shared identically
    // across every job link extracted from that same email. A digest email linking 20+ postings
    // (Indeed's "X. 21 more ... jobs in ..." pattern) can easily have a multi-KB body, so 12,000
    // chars of it was consistently pushing this call's token count over Groq's 8,000 TPM ceiling
    // on its own (413 "Request too large", ~8,300-8,400 tokens requested) — confirmed recurring
    // on the same oversized digest email across multiple separate runs.
    //
    // UPDATE: ImapAlertFetcher no longer hands every job link the whole email — it now extracts
    // a small (~1,200 char) windowed snippet around each job's own link (see its
    // ExtractJobSnippet/SnippetWindowChars remarks — this was also the fix for a real
    // Locations/WorkModes filtering bug, not just a token-budget one), so alert-mail RawText is
    // now typically short. 6,000 is kept as the cap here anyway: it's still a real backstop for
    // long RSS/HTML-board descriptions (which were never routed through ImapAlertFetcher, so
    // this constant was never solely about alert mail) and there's no upside to raising it back.
    private const int MaxRawTextChars = 6_000;

    // Free-tier rate limits (e.g. Groq's 8000 TPM) can be tight enough that a
    // single fit-score call eats most of a minute's budget, so a burst of
    // matched jobs runs straight into 429s. Rather than dropping those jobs
    // as unscored, retry them a bounded number of times, waiting for exactly
    // as long as the provider says to (see ParseRetryDelay) — this trades run
    // time for actually getting every matched job scored.
    private const int MaxRateLimitRetries = 6;
    private static readonly TimeSpan RetryDelayBuffer = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public LlmFitScorer(HttpClient http, LlmOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<FitScoreResult?> ScoreAsync(Job job, Preferences preferences, CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(job, preferences);

        for (var attempt = 0; attempt <= MaxRateLimitRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
                request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request, ct);
                var responseText = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                    {
                        var wait = ParseRetryDelay(response, responseText) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                        Log.Warning("[FitScore] {Title} @ {Company} -> rate limited (attempt {Attempt}/{Max}), waiting {Wait:0.0}s before retry",
                            job.Title, job.Company, attempt + 1, MaxRateLimitRetries + 1, wait.TotalSeconds);
                        await Task.Delay(wait, ct);
                        continue;
                    }

                    Log.Warning("[FitScore] {Title} @ {Company} -> {Endpoint} returned {Status}: {Body}",
                        job.Title, job.Company, _options.Endpoint, (int)response.StatusCode, Truncate(responseText, 500));
                    return null;
                }

                var content = ExtractMessageContent(responseText);
                if (content is null)
                {
                    Log.Warning("[FitScore] {Title} @ {Company} -> couldn't find message content in the response from {Endpoint}", job.Title, job.Company, _options.Endpoint);
                    return null;
                }

                var result = ParseScoreJson(content);
                if (result is null)
                {
                    Log.Warning("[FitScore] {Title} @ {Company} -> couldn't parse scoring JSON: {Content}", job.Title, job.Company, Truncate(content, 500));
                    return null;
                }

                Log.Debug("[FitScore] {Title} @ {Company} -> {Score} ({Reason})", job.Title, job.Company, result.FitScore, result.Reason);
                return result;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FitScore] {Title} @ {Company} -> exception, skipped", job.Title, job.Company);
                return null;
            }
        }

        Log.Warning("[FitScore] {Title} @ {Company} -> still rate limited after {Max} retries, skipped",
            job.Title, job.Company, MaxRateLimitRetries + 1);
        return null;
    }

    /// <summary>Prefers the exact wait the provider tells us over guessing: a standard
    /// Retry-After header if present, otherwise Groq's own "Please try again in 39.615s"
    /// (or "855ms") phrasing embedded in the 429 body. Falls back to null (caller uses
    /// exponential backoff) only if neither is present — e.g. a different provider's
    /// 429 shape.</summary>
    private static TimeSpan? ParseRetryDelay(HttpResponseMessage response, string responseBody)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta + RetryDelayBuffer;

        var match = Regex.Match(responseBody, @"try again in ([\d.]+)(ms|s)\b");
        if (!match.Success) return null;

        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var wait = match.Groups[2].Value == "ms" ? TimeSpan.FromMilliseconds(value) : TimeSpan.FromSeconds(value);
        return wait + RetryDelayBuffer;
    }

    private string BuildRequestBody(Job job, Preferences preferences)
    {
        var systemPrompt =
            "You are a job-fit scoring assistant inside a personal job-search tool. You are given a " +
            "candidate's profile/preferences and a single job posting. Score how well the job fits the " +
            "candidate from 0 (no fit) to 100 (ideal fit). Also extract, ONLY if actually stated in the " +
            "job text, a salary figure, a years-of-experience requirement, and a qualification/degree " +
            "requirement — use null for any of these not mentioned; never invent or infer a number that " +
            "isn't in the text. Respond with strict JSON only, no markdown fences, matching exactly this " +
            "shape: {\"fitScore\": <integer 0-100>, \"extractedSalary\": <string or null>, " +
            "\"extractedExperience\": <string or null>, \"extractedQualification\": <string or null>, " +
            "\"reason\": <one short sentence>}.";

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("## Candidate profile");
        userPrompt.AppendLine(string.IsNullOrWhiteSpace(preferences.ProfileSummary)
            ? "(no profile summary provided)"
            : preferences.ProfileSummary);
        userPrompt.AppendLine();
        userPrompt.AppendLine("## Candidate preferences");
        userPrompt.AppendLine($"Must-have skills: {string.Join(", ", preferences.MustHaveKeywords)}");
        userPrompt.AppendLine($"Nice-to-have skills: {string.Join(", ", preferences.NiceToHaveKeywords)}");
        if (preferences.SalaryFloorPerAnnum is not null)
            userPrompt.AppendLine($"Salary floor: {preferences.SalaryFloorPerAnnum} per annum");
        if (preferences.ExperienceMinYears is not null || preferences.ExperienceMaxYears is not null)
            userPrompt.AppendLine($"Target experience band: {preferences.ExperienceMinYears?.ToString() ?? "any"}-{preferences.ExperienceMaxYears?.ToString() ?? "any"} years");
        userPrompt.AppendLine(preferences.QualificationRequirement == QualificationRequirementMode.Match
            ? "Qualification mismatches should lower the score noticeably."
            : "Qualification mismatches should be noted but should NOT significantly lower the score.");
        userPrompt.AppendLine();
        userPrompt.AppendLine("## Job posting");
        userPrompt.AppendLine($"Title: {job.Title}");
        userPrompt.AppendLine($"Company: {job.Company}");
        if (!string.IsNullOrWhiteSpace(job.Location)) userPrompt.AppendLine($"Location: {job.Location}");
        userPrompt.AppendLine("Description:");
        userPrompt.AppendLine(Truncate(job.RawText ?? string.Empty, MaxRawTextChars));

        var payload = new
        {
            model = _options.Model,
            response_format = new { type = "json_object" },
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt.ToString() },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Pulls choices[0].message.content out of the chat-completions response shape without
    /// pulling in a full provider-specific response DTO — this is the only field this class needs,
    /// and every OpenAI-compatible provider (OpenAI, Groq, Gemini's compat endpoint, Ollama) returns
    /// it in this same place.</summary>
    private static string? ExtractMessageContent(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return null;
        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var contentEl))
            return null;
        return contentEl.GetString();
    }

    private static FitScoreResult? ParseScoreJson(string content)
    {
        // Models occasionally wrap JSON in ```json fences even when told not
        // to — strip them rather than fail the whole call over formatting.
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
        }

        using var doc = JsonDocument.Parse(trimmed);
        var root = doc.RootElement;

        int? fitScore = root.TryGetProperty("fitScore", out var scoreEl) && scoreEl.ValueKind is JsonValueKind.Number
            ? Math.Clamp(scoreEl.GetInt32(), 0, 100)
            : null;

        return new FitScoreResult(
            fitScore,
            GetNullableString(root, "extractedSalary"),
            GetNullableString(root, "extractedExperience"),
            GetNullableString(root, "extractedQualification"),
            GetNullableString(root, "reason"));
    }

    private static string? GetNullableString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + " …(truncated)";
}