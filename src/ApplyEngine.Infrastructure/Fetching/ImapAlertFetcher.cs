using System.Net;
using System.Text.RegularExpressions;
using ApplyEngine.Domain.Entities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Serilog;

namespace ApplyEngine.Infrastructure.Fetching;

public class ImapOptions
{
    public required string Host { get; set; }
    public int Port { get; set; } = 993;
    public required string Username { get; set; }
    public required string Password { get; set; }

    /// <summary>Only read alert mail from these senders (spec: LinkedIn/Naukri/Indeed job-alert addresses).</summary>
    public List<string> TrustedSenderDomains { get; set; } = new() { "linkedin.com", "naukri.com", "indeed.com" };

    /// <summary>Move processed alert emails here so the next run doesn't re-read them. Set null to only mark as \Seen instead.</summary>
    public string? ProcessedFolderName { get; set; } = "ApplyEngine-Processed";
}

/// <summary>
/// Reads job-alert emails over IMAP (spec §3 Layer 1, "the clever bit").
/// This is the one fetcher that isn't scraping anything: LinkedIn/Naukri/
/// Indeed do the crawling when they build your alert email, we just parse
/// the links out of an email that's already sitting in your own inbox.
/// </summary>
public class ImapAlertFetcher : ISourceFetcher
{
    private readonly ImapOptions _options;
    private static readonly Regex HrefRegex = new(
        @"href\s*=\s*[""'](https?://[^""'\s]+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Subject-line phrases LinkedIn/Naukri/Indeed use for "someone messaged
    /// you" notifications, as opposed to "new jobs matching your search"
    /// alerts. Not exhaustive — platforms change wording — so this is a
    /// best-effort net, not a guarantee every message notification is caught.
    /// </summary>
    private static readonly string[] MessageNotificationPhrases =
    {
        "new message", "sent you a message", "messaged you", "you have a message",
        "new inmail", "unread message", "recruiter update", "wants to connect",
    };

    public SourceType HandlesType => SourceType.ImapAlert;

    /// <summary>
    /// UIDs read (but not yet marked processed) by the most recent FetchAsync
    /// call. Deliberately NOT touched during FetchAsync itself — see
    /// ConfirmProcessedAsync for why.
    /// </summary>
    private List<UniqueId> _pendingUids = new();

    public ImapAlertFetcher(ImapOptions options)
    {
        _options = options;
    }

    public async Task<List<Job>> FetchAsync(Source source, CancellationToken ct = default)
    {
        var jobs = new List<Job>();
        var pendingUids = new List<UniqueId>();

        using var client = new ImapClient
        {
            // Default MailKit timeout can run to 2 minutes — too long to sit
            // silently on a stalled connection. 20s: generous for a slow
            // network, short enough that a real problem surfaces as a clear
            // error instead of looking like a hang.
            Timeout = 20_000,
        };

        Log.Information("[{Source}] IMAP: connecting to {Host}:{Port}...", source.Name, _options.Host, _options.Port);
        await client.ConnectAsync(_options.Host, _options.Port, MailKit.Security.SecureSocketOptions.SslOnConnect, ct);
        Log.Information("[{Source}] IMAP: connected, authenticating as {Username}...", source.Name, _options.Username);
        await client.AuthenticateAsync(_options.Username, _options.Password, ct);
        Log.Information("[{Source}] IMAP: authenticated.", source.Name);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

        // Filter by sender ON THE SERVER before downloading anything. Without
        // this, a large real-world inbox (hundreds of unread LinkedIn/Naukri
        // notifications that are NOT job alerts) means downloading every one
        // of them just to check its sender, and worse, marking all of them
        // read/moved as a side effect even though most were never job alerts.
        var trustedSenderQuery = BuildTrustedSenderQuery(_options.TrustedSenderDomains);
        var unseen = await inbox.SearchAsync(SearchQuery.NotSeen.And(trustedSenderQuery), ct);
        Log.Information("[{Source}] IMAP: {UnseenCount} unseen message(s) from a trusted sender domain.", source.Name, unseen.Count);

        foreach (var uid in unseen)
        {
            var message = await inbox.GetMessageAsync(uid, ct);
            var fromDomain = ExtractSenderDomain(message);

            if (!_options.TrustedSenderDomains.Any(d => fromDomain.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Debug("[{Source}] IMAP: skipping message from untrusted sender {FromDomain} — subject: {Subject}",
                    source.Name, fromDomain, message.Subject);
                continue; // not a job-alert sender we trust — leave unread, ignore.
            }

            var extracted = ExtractJobsFromAlertEmail(message, source.Name);

            // No job links found — before giving up on this email, check
            // whether it's a "someone messaged you" notification instead of
            // a job alert. Without this, message-notification emails were
            // silently marked read/moved with nothing to show for them.
            if (extracted.Count == 0 && LooksLikeMessageNotification(message))
            {
                extracted.Add(BuildMessageAlert(message, fromDomain, source.Name));
                Log.Information("[{Source}] IMAP: message from {FromDomain} (subject: {Subject}) — recognized as a message notification.",
                    source.Name, fromDomain, message.Subject);
            }
            else
            {
                Log.Information("[{Source}] IMAP: message from {FromDomain} (subject: {Subject}) — extracted {LinkCount} job link(s).",
                    source.Name, fromDomain, message.Subject, extracted.Count);
            }

            jobs.AddRange(extracted);

            // NOT marking Seen / moving here anymore. Doing that immediately
            // meant a crash later (e.g. the DB save failing) left the email
            // already consumed with nothing durably stored — real data loss,
            // twice. Instead we just remember which UIDs we read; the caller
            // marks them processed via ConfirmProcessedAsync(), and only
            // after it has confirmed the extracted jobs were saved.
            pendingUids.Add(uid);
        }

        await client.DisconnectAsync(true, ct);
        _pendingUids = pendingUids;
        return jobs;
    }

    /// <summary>
    /// Marks every message read by the most recent FetchAsync call as
    /// processed (\Seen + moved to ProcessedFolderName). Call this ONLY after
    /// the caller has confirmed the extracted jobs were durably saved (e.g.
    /// db.SaveChangesAsync() returned successfully) — that's the whole
    /// point: if the save fails, this is simply never called, so the
    /// messages stay unread in the inbox and get safely re-read next run
    /// (harmless — DedupeService absorbs the resulting duplicates) instead
    /// of being silently lost.
    /// </summary>
    public async Task ConfirmProcessedAsync(CancellationToken ct = default)
    {
        if (_pendingUids.Count == 0) return;

        using var client = new ImapClient { Timeout = 20_000 };
        await client.ConnectAsync(_options.Host, _options.Port, MailKit.Security.SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(_options.Username, _options.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

        await inbox.AddFlagsAsync(_pendingUids, MessageFlags.Seen, true, ct);

        if (_options.ProcessedFolderName is not null)
        {
            var processedFolder = await GetOrCreateProcessedFolderAsync(client, ct);
            await inbox.MoveToAsync(_pendingUids, processedFolder, ct);
        }

        await client.DisconnectAsync(true, ct);
        Log.Information("IMAP: confirmed {Count} message(s) as processed after a successful save.", _pendingUids.Count);
        _pendingUids = new();
    }

    /// <summary>
    /// Alert emails are HTML with one block per job listing. We don't try to
    /// fully parse LinkedIn/Naukri/Indeed's markup (it changes without notice) —
    /// instead we pull every outbound link that looks like a job-view URL and a
    /// nearby title, which is far more resilient to template changes.
    /// </summary>
    private static List<Job> ExtractJobsFromAlertEmail(MimeMessage message, string sourceName)
    {
        var html = message.HtmlBody ?? message.TextBody ?? string.Empty;
        var jobs = new List<Job>();

        foreach (System.Text.RegularExpressions.Match m in HrefRegex.Matches(html))
        {
            var url = m.Groups[1].Value;
            if (!LooksLikeJobLink(url)) continue;

            jobs.Add(new Job
            {
                // Alert emails rarely give a clean isolated title per link via regex alone;
                // Phase 0 stores the subject as a placeholder title and relies on the LLM
                // extraction step (Phase 2) — or a human, during Approve — to read the real
                // title off the page. Good enough to surface the link in the daily digest.
                Title = message.Subject ?? "(from alert email)",
                Company = "Unknown — see link",
                Url = url,
                RawText = ExtractJobSnippet(html, m.Index),
                SourceName = sourceName,
            });
        }

        return jobs;
    }

    // FIX (Locations/WorkModes filter gap for alert-mail-sourced jobs): this
    // used to hand every job link in a digest email the SAME RawText — the
    // whole message.TextBody. For a single-job alert that's harmless, but
    // LinkedIn/Naukri/Indeed digests routinely bundle several unrelated
    // postings into one email. Job.Location is never set for these (there's
    // no clean structured field to pull it from), so KeywordFilter's
    // Locations/WorkModes checks fall back to scanning Location+RawText (see
    // its remarks) — which meant they were really asking "does ANY job
    // anywhere in this digest mention a preferred location/work mode", not
    // "does THIS one". A single Bangalore-only preference could silently
    // wave through a Mumbai posting just because some OTHER job in the same
    // email happened to say "Bangalore" or "Remote" — a real false-positive
    // (and, symmetrically, false-negative) gap, not just noisy text.
    //
    // Fix: give each job link its own small window of the surrounding HTML
    // (stripped of tags) as RawText instead of the entire email. Most
    // alert-email templates lay out one job as a self-contained "row" —
    // title, company, location, sometimes a one-line blurb — clustered
    // tightly around its own link, so a window this size reliably captures
    // that job's own text without usually spilling into a neighboring job's
    // row in the same digest. Not perfect (a template with very short rows
    // packed close together could still leak a neighbor's text at the
    // edges), but a large, verifiable improvement over "the whole email" —
    // and it's the same best-effort/degrades-independently spirit as
    // everything else in this fetcher, not a promise of exact per-job
    // isolation.
    private const int SnippetWindowChars = 600;

    private static string ExtractJobSnippet(string html, int matchIndex)
    {
        var start = Math.Max(0, matchIndex - SnippetWindowChars);
        var end = Math.Min(html.Length, matchIndex + SnippetWindowChars);
        var window = html[start..end];
        return StripHtmlToPlainText(window);
    }

    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Strips tags and collapses whitespace so the snippet reads as plain
    /// text for KeywordFilter's substring matching — harmless to call on
    /// already-plain text too (the TextBody fallback case), since there are
    /// no tags to strip and this just normalizes whitespace.
    /// </summary>
    private static string StripHtmlToPlainText(string html)
    {
        var text = TagRegex.Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static bool LooksLikeJobLink(string url) =>
        // LinkedIn: only an actual job-posting page, e.g.
        // "linkedin.com/comm/jobs/view/4446739359/?...". Deliberately NOT a
        // bare "/jobs/" substring match — that also caught
        // "/jobs/search-results/?..." (a search query, not one posting) and
        // "/jobs/alerts?..." (the "manage your alerts" footer link), both of
        // which showed up once or twice per alert email and got stored as
        // fake "jobs" with no real posting behind them.
        url.Contains("/jobs/view/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/viewjob", StringComparison.OrdinalIgnoreCase)
        || url.Contains("job-details", StringComparison.OrdinalIgnoreCase)
        // Indeed's alert emails don't link straight to /viewjob — every link is
        // wrapped in a click-tracking redirect like
        // "in.indeed.com/rc/clk/dl?jk=...&from=ja&...". The tracking domain/path
        // can vary, but "jk=" (Indeed's job id) reliably shows up in both the
        // wrapped and unwrapped forms, so match on that instead of the path.
        || url.Contains("jk=", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if the subject looks like "you have a new message" rather than a
    /// job-alert digest. Checked only after job-link extraction comes back
    /// empty, so a job alert that happens to mention "message" somewhere in
    /// its body is never misclassified.
    /// </summary>
    private static bool LooksLikeMessageNotification(MimeMessage message)
    {
        var subject = message.Subject ?? string.Empty;
        return MessageNotificationPhrases.Any(phrase =>
            subject.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds a placeholder "Job" entry for a message notification so it can
    /// flow through the same normalize/dedupe/digest pipeline as real
    /// postings. DigestBuilder and KeywordFilter both check job.Kind to treat
    /// these differently (skip the preference filter, show in their own
    /// digest section) — see Job.cs and KeywordFilter.cs.
    /// </summary>
    private static Job BuildMessageAlert(MimeMessage message, string fromDomain, string sourceName)
    {
        var platform = PlatformNameFor(fromDomain);
        var snippet = (message.TextBody ?? string.Empty).Trim();
        if (snippet.Length > 200)
            snippet = snippet[..200] + "...";

        return new Job
        {
            Kind = JobKind.MessageAlert,
            Title = $"New message on {platform} — {message.Subject ?? "(no subject)"}",
            Company = platform,
            // Job.Url is required even though a message notification rarely
            // links anywhere useful — this becomes part of the dedupe hash,
            // so it needs to be unique per email rather than a fixed string.
            Url = $"mailto:{fromDomain}#{message.MessageId ?? Guid.NewGuid().ToString()}",
            RawText = string.IsNullOrWhiteSpace(snippet) ? "(no preview text in the email — open the app to read it.)" : snippet,
            SourceName = sourceName,
        };
    }

    private static string PlatformNameFor(string fromDomain)
    {
        if (fromDomain.EndsWith("linkedin.com", StringComparison.OrdinalIgnoreCase)) return "LinkedIn";
        if (fromDomain.EndsWith("naukri.com", StringComparison.OrdinalIgnoreCase)) return "Naukri";
        if (fromDomain.EndsWith("indeed.com", StringComparison.OrdinalIgnoreCase)) return "Indeed";
        return fromDomain;
    }

    private static string ExtractSenderDomain(MimeMessage message)
    {
        var address = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
        var at = address.IndexOf('@');
        return at < 0 ? string.Empty : address[(at + 1)..];
    }

    /// <summary>
    /// Builds "From contains domain1 OR From contains domain2 OR ..." so the
    /// IMAP server does the sender filtering, not us — the difference between
    /// downloading 829 messages to find the handful that matter, versus only
    /// ever touching the handful. Throws if TrustedSenderDomains is empty,
    /// since an unfiltered search would silently fall back to "match everything."
    /// </summary>
    private static SearchQuery BuildTrustedSenderQuery(List<string> domains)
    {
        if (domains.Count == 0)
            throw new InvalidOperationException(
                "ImapOptions.TrustedSenderDomains is empty — refusing to search with no sender filter (that would match every unseen email in the inbox).");

        // Explicitly typed as the base SearchQuery, not "var" — SearchQuery.FromContains(...)
        // returns the narrower TextSearchQuery, but .Or(...) below returns a
        // BinarySearchQuery once there's more than one trusted domain. With "var"
        // inferring TextSearchQuery from the first line, reassigning the .Or(...)
        // result back into it fails to compile (CS0029: can't implicitly convert
        // BinarySearchQuery to TextSearchQuery).
        SearchQuery query = SearchQuery.FromContains(domains[0]);
        foreach (var domain in domains.Skip(1))
            query = query.Or(SearchQuery.FromContains(domain));
        return query;
    }

    private async Task<MailKit.IMailFolder> GetOrCreateProcessedFolderAsync(ImapClient client, CancellationToken ct)
    {
        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        try
        {
            return await personal.GetSubfolderAsync(_options.ProcessedFolderName!, ct);
        }
        catch (FolderNotFoundException)
        {
            return await personal.CreateAsync(_options.ProcessedFolderName!, true, ct);
        }
    }
}