using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyEngine.Domain.Entities;
using ApplyEngine.Infrastructure.Scoring;
using Serilog;

namespace ApplyEngine.Infrastructure.Resumes;

/// <summary>
/// Spec §5's resume tailoring engine: base resume variant (already the closest of the three —
/// see ResumeVariantSelector) + a specific job posting → a lightly reworded version of that
/// same resume, same facts, emphasis shifted toward the posting. Deliberately NOT a
/// from-scratch resume generator — the system prompt is explicit that no experience, dates,
/// employers, or skills may be invented, only reordered/reworded/re-emphasized, so the output
/// stays truthful to what's actually on the candidate's real resume.
///
/// Only ever called for matches that already cleared both KeywordFilter's free hard filter and
/// LlmFitScorer's paid fit-score bar (MatchStatus.Queued) — see JobPipelineService — so this
/// second LLM call per job is naturally bounded to just the handful of postings actually worth
/// tailoring a resume for, not every scored match.
///
/// Shares LlmOptions/the same Groq (or other OpenAI-compatible) endpoint as LlmFitScorer rather
/// than needing a separate provider config, and duplicates (rather than shares) that class's
/// 429-retry/ParseRetryDelay logic on purpose — LlmFitScorer's retry behavior is already
/// depended on in production; keeping this class self-contained means a future change to one
/// doesn't risk silently changing the other's already-verified behavior.
/// </summary>
public class ResumeTailor
{
    // ROUND 2 CUT: 6,000 -> 3,000. The 3,000-token MaxOutputTokens tried below was STILL not
    // enough — real output was observed cutting off mid-way through the third PROFESSIONAL
    // EXPERIENCE entry, with the Qualifications and Certifications sections never generated at
    // all. That means the model, despite the "do not lengthen" instruction, is writing bullets
    // noticeably more verbose than the static base resume text (more detail per bullet than the
    // source file has), so the honest fix is to give the output a much bigger budget rather than
    // inching it up again — and the only place left to take that budget from (short of raising
    // the whole request past Groq's ceiling) is the job description. Most postings put everything
    // that actually matters for tailoring (title, required skills, responsibilities) in their
    // first third; the tail end (boilerplate EEO text, benefits blurbs, "how to apply" footers)
    // was costing tokens without helping the tailoring. 3,000 chars (~750 tokens) still comfortably
    // covers the substantive part of nearly any posting.
    private const int MaxRawTextChars = 3_000;

    // ROUND 2: 1,800 -> 3,000 wasn't enough either (see MaxRawTextChars' remarks for what the
    // latest screenshots showed: cut off mid-third-experience-entry, Qualifications and
    // Certifications never even started). Rather than guess again in small steps, this jumps
    // straight to a generous ceiling: 4,500 tokens is enough for a resume noticeably LONGER than
    // any of the three ~1,800-1,950-token base variants, which is what's actually needed since the
    // model's real bullets are running more verbose than the source text, not shorter or equal to
    // it as originally assumed.
    // New total reserved budget: ~1,950 (longest base resume) + ~750 (job description, now capped
    // at MaxRawTextChars=3,000 chars) + ~300 (system/user prompt scaffolding) + 4,500 (this output
    // cap) = ~7,500 tokens — still under Groq's 8,000 TPM ceiling, with the cut to MaxRawTextChars
    // above making room for the bigger output cap instead of just stacking on top of the old total.
    // If this is STILL not enough once verified against real output, the next lever is dropping
    // MaxRawTextChars further (e.g. to 2,000 chars / ~500 tokens) to buy more output headroom —
    // the job description has more slack to give than the resume does, since the resume is the
    // actual deliverable here and the job posting is only ever used for context.
    private const int MaxOutputTokens = 4_500;
    private const int MaxRateLimitRetries = 6;
    private static readonly TimeSpan RetryDelayBuffer = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public ResumeTailor(HttpClient http, LlmOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<string?> TailorAsync(string baseResumeMarkdown, Job job, CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(baseResumeMarkdown, job);

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
                        Log.Warning("[ResumeTailor] {Title} @ {Company} -> rate limited (attempt {Attempt}/{Max}), waiting {Wait:0.0}s before retry",
                            job.Title, job.Company, attempt + 1, MaxRateLimitRetries + 1, wait.TotalSeconds);
                        await Task.Delay(wait, ct);
                        continue;
                    }

                    Log.Warning("[ResumeTailor] {Title} @ {Company} -> {Endpoint} returned {Status}: {Body}",
                        job.Title, job.Company, _options.Endpoint, (int)response.StatusCode, Truncate(responseText, 500));
                    return null;
                }

                var (content, finishReason) = ExtractMessageContent(responseText);
                if (content is null)
                {
                    Log.Warning("[ResumeTailor] {Title} @ {Company} -> couldn't find message content in the response from {Endpoint}", job.Title, job.Company, _options.Endpoint);
                    return null;
                }

                // Groq (like OpenAI) sets finish_reason "length" when max_tokens cut the
                // completion off before the model was actually done — this is exactly the bug
                // that produced a resume silently truncated mid-PROFESSIONAL-SUMMARY (see
                // MaxOutputTokens' remarks). Raising that constant should make this rare, but
                // this check exists so if a future resume variant is long enough to hit it
                // again, it shows up as a clear warning instead of shipping a broken resume
                // with no trace in the logs.
                if (finishReason == "length")
                {
                    Log.Warning("[ResumeTailor] {Title} @ {Company} -> response was cut off by max_tokens before finishing ({Length} chars generated) — resume is likely incomplete; consider raising MaxOutputTokens",
                        job.Title, job.Company, content.Length);
                }

                var trimmed = content.Trim();
                if (trimmed.Length == 0)
                {
                    // Seen in production: the model can return a 200 OK with a message whose
                    // content is an empty (or whitespace-only) string. `content is null` above
                    // doesn't catch this — it's a real, non-null string, just blank — so without
                    // this check an empty tailored resume was silently treated as a success and
                    // stored on the match, then handed to ApplicationEmailDrafter to draft an
                    // email "referencing" nothing. Treat it the same as every other failure path
                    // here: log and return null so the caller's null-check skips this job.
                    Log.Warning("[ResumeTailor] {Title} @ {Company} -> model returned an empty tailored resume, treating as failure", job.Title, job.Company);
                    return null;
                }

                Log.Debug("[ResumeTailor] {Title} @ {Company} -> tailored resume generated ({Length} chars)", job.Title, job.Company, trimmed.Length);
                return trimmed;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ResumeTailor] {Title} @ {Company} -> exception, skipped", job.Title, job.Company);
                return null;
            }
        }

        Log.Warning("[ResumeTailor] {Title} @ {Company} -> still rate limited after {Max} retries, skipped",
            job.Title, job.Company, MaxRateLimitRetries + 1);
        return null;
    }

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

    private string BuildRequestBody(string baseResumeMarkdown, Job job)
    {
        var systemPrompt =
            "You are a resume-tailoring assistant inside a personal job-search tool. You are given a " +
            "candidate's real resume (Markdown) and a single job posting. Rewrite the resume so it reads " +
            "as tailored to this specific posting: reorder bullet points so the most relevant ones come " +
            "first within each role, rephrase the PROFESSIONAL SUMMARY to foreground the skills/keywords " +
            "this posting cares about, and lightly reword bullets to use the posting's own terminology " +
            "where it genuinely matches something the candidate already did. " +
            "STRICT RULES: never invent experience, employers, job titles, dates, degrees, certifications, " +
            "or skills that are not already present in the given resume; never change any employer name, " +
            "date range, or numeric claim (percentages, counts) from the original; do not lengthen the " +
            "resume beyond its original scope — trim rather than pad. " +
            "HEADER RULE (do not skip this): the very first line(s) of the resume are the candidate's name " +
            "and contact/header block (location, email, phone, and any [text](url) Markdown links such as " +
            "LinkedIn, GitHub, or a portfolio site). Copy this header EXACTLY as it appears in the input, " +
            "character-for-character, including every [text](url) link in its original Markdown link " +
            "syntax — never omit it, never shorten it, never convert a link to plain text, and never treat " +
            "it as trimmable padding. The header is the one part of this resume that must never change. " +
            "Respond with ONLY the tailored resume as Markdown, no commentary before or after, no code fences.";

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("## Candidate's current resume (Markdown)");
        userPrompt.AppendLine(baseResumeMarkdown);
        userPrompt.AppendLine();
        userPrompt.AppendLine("## Job posting to tailor toward");
        userPrompt.AppendLine($"Title: {job.Title}");
        userPrompt.AppendLine($"Company: {job.Company}");
        if (!string.IsNullOrWhiteSpace(job.Location)) userPrompt.AppendLine($"Location: {job.Location}");
        userPrompt.AppendLine("Description:");
        userPrompt.AppendLine(Truncate(job.RawText ?? string.Empty, MaxRawTextChars));

        var payload = new
        {
            model = _options.Model,
            temperature = 0.3,
            // Without this, Groq reserves an implicit (large) output-token budget against the
            // 8,000 TPM ceiling before it even runs the request, which was the other half of the
            // 413 cause — capping it here is what actually keeps "Requested" under the limit,
            // not just the MaxRawTextChars trim above.
            max_tokens = MaxOutputTokens,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt.ToString() },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private static (string? Content, string? FinishReason) ExtractMessageContent(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return (null, null);
        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var contentEl))
            return (null, null);

        var finishReason = first.TryGetProperty("finish_reason", out var finishReasonEl) && finishReasonEl.ValueKind == JsonValueKind.String
            ? finishReasonEl.GetString()
            : null;

        return (contentEl.GetString(), finishReason);
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + " …(truncated)";
}