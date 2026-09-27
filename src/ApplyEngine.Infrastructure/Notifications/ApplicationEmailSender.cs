using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ApplyEngine.Infrastructure.Notifications;

/// <summary>
/// Spec §7 Channel A: actually sends a drafted application email (ApplicationEmailDrafter) with
/// the tailored resume (ResumeTailor) attached, to whatever address ApplyEmailExtractor found for
/// that match. Deliberately separate from SmtpDigestSender even though both share SmtpOptions and
/// the same MailKit plumbing — see that class's remarks: a missed digest is a minor annoyance, a
/// missed (or wrongly-sent) job application is not, so this stays its own small class rather than
/// growing a "send mode" flag onto the digest sender.
///
/// Attaches the tailored resume as a real PDF, rendered server-side by ResumePdfRenderer (see
/// its remarks — QuestPDF, mirrors dashboard/lib/markdown.ts's Markdown subset). This used to
/// attach the raw tailored-resume Markdown as a plain-text (.txt) file, back when there was no
/// server-side PDF renderer and the only PDF path was manually clicking Download PDF in the
/// browser and using Chrome's print-to-PDF — that limitation is gone now, so callers pass
/// already-rendered PDF bytes here instead of the Markdown string.
/// </summary>
public class ApplicationEmailSender
{
    private readonly SmtpOptions _options;

    public ApplicationEmailSender(SmtpOptions options)
    {
        _options = options;
    }

    public async Task SendAsync(
        string toAddress,
        string subject,
        string plainTextBody,
        string resumeAttachmentFileName,
        byte[] resumePdfBytes,
        CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;

        var builder = new BodyBuilder { TextBody = plainTextBody };
        builder.Attachments.Add(
            resumeAttachmentFileName,
            resumePdfBytes,
            ContentType.Parse("application/pdf"));
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(_options.Username, _options.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}