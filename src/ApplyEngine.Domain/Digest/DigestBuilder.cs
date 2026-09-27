using System.Text;
using ApplyEngine.Domain.Entities;

namespace ApplyEngine.Domain.Digest;

/// <summary>
/// Formats the Phase 0 output described in the build map: "email yourself a
/// daily digest of matches." Plain text on purpose — Phase 1 replaces this
/// with the dashboard review queue; this just needs to be readable in an inbox.
/// </summary>
public static class DigestBuilder
{
    public static string BuildPlainText(IReadOnlyList<Job> matches, DateTime dateUtc)
    {
        var jobs = matches.Where(j => j.Kind == JobKind.JobPosting).ToList();
        var messages = matches.Where(j => j.Kind == JobKind.MessageAlert).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Job digest — {dateUtc:yyyy-MM-dd} ({jobs.Count} new match{(jobs.Count == 1 ? "" : "es")}, {messages.Count} message{(messages.Count == 1 ? "" : "s")})");
        sb.AppendLine(new string('-', 60));

        if (jobs.Count == 0)
        {
            sb.AppendLine("No new roles matched your preferences today.");
        }
        else
        {
            foreach (var job in jobs.OrderByDescending(j => j.PostedAtUtc))
            {
                sb.AppendLine();
                sb.AppendLine($"{job.Title} — {job.Company}");
                if (!string.IsNullOrWhiteSpace(job.Location))
                    sb.AppendLine($"  Location: {job.Location}");
                sb.AppendLine($"  Source:   {job.SourceName}");
                sb.AppendLine($"  Link:     {job.Url}");
            }
        }

        if (messages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('-', 60));
            sb.AppendLine("Messages waiting for you");
            sb.AppendLine(new string('-', 60));
            foreach (var msg in messages.OrderByDescending(j => j.FirstSeenAtUtc))
            {
                sb.AppendLine();
                sb.AppendLine($"{msg.Title}");
                if (!string.IsNullOrWhiteSpace(msg.RawText))
                    sb.AppendLine($"  {msg.RawText}");
                sb.AppendLine($"  Source: {msg.SourceName}");
            }
        }

        return sb.ToString();
    }
}