using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyEngine.Domain.Entities;
using ApplyEngine.Infrastructure.Scoring;
using Serilog;

namespace ApplyEngine.Infrastructure.Emails;

/// <summary>Subject + body for one drafted application email — see ApplicationEmailDrafter.</summary>
public record ApplicationEmailDraft(string Subject, string Body);

/// <summary>
/// Spec §6's ATS email drafting: a specific job posting + this candidate's already-tailored
/// resume (see ResumeTailor) → a short, ready-to-review application email (subject + body).
/// Deliberately drafting only — nothing here sends anything or touches a mailbox; that's
/// Channel A in spec §7, a separate later piece. The resume itself is only *referenced* in the
/// body text for now ("my tailored resume is attached"), not actually attached as a file — real
/// attachment happens once the send step exists and needs a file to attach.
///
/// Only ever called for matches that already have both a Queued status and a successfully
/// tailored resume (see JobPipelineService) — same bounded-cost shape as ResumeTailor: this is
/// the third LLM call per job, but only for the handful that made it all the way through both
/// earlier gates.
///
/// Shares LlmOptions/the same endpoint as LlmFitScorer and ResumeTailor (no separate provider
/// config) and duplicates rather than shares their 429-retry/ParseRetryDelay logic, for the same
/// reason ResumeTailor does: keeping each class self-contained means a future change to one
/// doesn't risk silently changing another's already-verified behavior.
/// </summary>
public class ApplicationEmailDrafter
{
    // Smaller than ResumeTailor's 6,000 on purpose — this prompt only needs enough of the JD and
    // the tailored resume to reference a couple of specific, genuine qualifications, not the full
    // text of either. Keeping both truncation bounds tight here leaves generous headroom under
    // Groq's 8,000 TPM ceiling even as the third LLM call in the pipeline for a given job.
    private const int MaxRawTextChars = 3_000;
    private const int MaxResumeChars = 2_500;

    // Raised from an original 700 after a real run failed with Groq's 400 "Failed to generate
    // JSON... max completion tokens reached before generating a valid document" — with
    // response_format:json_object, the model has to emit a complete, well-formed JSON object
    // before it stops; cutting it off mid-string with too tight a max_tokens produces invalid
    // JSON and a hard error instead of a graceful truncation. 700 was sized for a short email
    // but didn't leave enough room for subject + a 3-4 paragraph body inside the JSON
    // envelope. 1,200 leaves real margin while staying far under the 8,000 TPM ceiling given
    // how small this call's input already is (see MaxRawTextChars/MaxResumeChars above).
    private const int MaxOutputTokens = 1_200;
    private const int MaxRateLimitRetries = 6;
    private static readonly TimeSpan RetryDelayBuffer = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    /// <summary>Null/blank is fine — the drafted email just signs off with a "[Your Name]"
    /// placeholder for you to fill in by hand, same "degrades independently" treatment as every
    /// other optional piece of config in this project.</summary>
    private readonly string? _candidateName;

    public ApplicationEmailDrafter(HttpClient http, LlmOptions options, string? candidateName = null)
    {
        _http = http;
        _options = options;
        _candidateName = candidateName;
    }

    public async Task<ApplicationEmailDraft?> DraftAsync(Job job, string tailoredResumeMarkdown, CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(job, tailoredResumeMarkdown);

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
                        Log.Warning("[EmailDraft] {Title} @ {Company} -> rate limited (attempt {Attempt}/{Max}), waiting {Wait:0.0}s before retry",
                            job.Title, job.Company, attempt + 1, MaxRateLimitRetries + 1, wait.TotalSeconds);
                        await Task.Delay(wait, ct);
                        continue;
                    }

                    Log.Warning("[EmailDraft] {Title} @ {Company} -> {Endpoint} returned {Status}: {Body}",
                        job.Title, job.Company, _options.Endpoint, (int)response.StatusCode, Truncate(responseText, 500));
                    return null;
                }

                var content = ExtractMessageContent(responseText);
                if (content is null)
                {
                    Log.Warning("[EmailDraft] {Title} @ {Company} -> couldn't find message content in the response from {Endpoint}", job.Title, job.Company, _options.Endpoint);
                    return null;
                }

                var draft = ParseDraftJson(content);
                if (draft is null)
                {
                    Log.Warning("[EmailDraft] {Title} @ {Company} -> couldn't parse email JSON: {Content}", job.Title, job.Company, Truncate(content, 500));
                    return null;
                }

                Log.Debug("[EmailDraft] {Title} @ {Company} -> drafted ({SubjectLength} char subject, {BodyLength} char body)",
                    job.Title, job.Company, draft.Subject.Length, draft.Body.Length);
                return draft;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[EmailDraft] {Title} @ {Company} -> exception, skipped", job.Title, job.Company);
                return null;
            }
        }

        Log.Warning("[EmailDraft] {Title} @ {Company} -> still rate limited after {Max} retries, skipped",
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

    private string BuildRequestBody(Job job, string tailoredResumeMarkdown)
    {
        var signOff = string.IsNullOrWhiteSpace(_candidateName) ? "[Your Name]" : _candidateName;

        var systemPrompt =
            "You are an assistant drafting a short job-application email inside a personal job-search " +
            "tool. You are given a job posting and the candidate's own (already-tailored) resume text. " +
            "Write a brief, professional application email: a clear subject line naming the role, and a " +
            "body of 3-4 short paragraphs that expresses genuine interest in the role, references 1-2 " +
            "specific qualifications that are ACTUALLY present in the given resume text (never invent " +
            "experience, employers, or skills not in it), mentions that a tailored resume is attached, " +
            $"and signs off with \"{signOff}\". Keep the tone warm but concise — this is an application " +
            "email, not a cover letter essay. Respond with strict JSON only, no markdown fences, matching " +
            "exactly this shape: {\"subject\": <string>, \"body\": <string>}.";

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("## Job posting");
        userPrompt.AppendLine($"Title: {job.Title}");
        userPrompt.AppendLine($"Company: {job.Company}");
        if (!string.IsNullOrWhiteSpace(job.Location)) userPrompt.AppendLine($"Location: {job.Location}");
        userPrompt.AppendLine("Description:");
        userPrompt.AppendLine(Truncate(job.RawText ?? string.Empty, MaxRawTextChars));
        userPrompt.AppendLine();
        userPrompt.AppendLine("## Candidate's tailored resume (Markdown, for this same job)");
        userPrompt.AppendLine(Truncate(tailoredResumeMarkdown, MaxResumeChars));

        var payload = new
        {
            model = _options.Model,
            response_format = new { type = "json_object" },
            temperature = 0.3,
            max_tokens = MaxOutputTokens,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt.ToString() },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

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

    private static ApplicationEmailDraft? ParseDraftJson(string content)
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

        var subject = root.TryGetProperty("subject", out var subjectEl) && subjectEl.ValueKind == JsonValueKind.String
            ? subjectEl.GetString()
            : null;
        var body = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
            ? bodyEl.GetString()
            : null;

        return subject is not null && body is not null ? new ApplicationEmailDraft(subject, body) : null;
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + " …(truncated)";
}