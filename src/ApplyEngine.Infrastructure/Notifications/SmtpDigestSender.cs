using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ApplyEngine.Infrastructure.Notifications;

public class SmtpOptions
{
    public required string Host { get; set; }
    public int Port { get; set; } = 587;
    public required string Username { get; set; }
    public required string Password { get; set; }
    public required string FromAddress { get; set; }
    public required string ToAddress { get; set; }
}

/// <summary>
/// Sends the Phase 0 daily digest to yourself. Deliberately separate from
/// whatever later sends job applications (spec §5 Channel A) — different
/// throttle policy, different failure tolerance (a missed digest is a minor
/// annoyance; a missed application send is not).
/// </summary>
public class SmtpDigestSender
{
    private readonly SmtpOptions _options;

    public SmtpDigestSender(SmtpOptions options)
    {
        _options = options;
    }

    public async Task SendAsync(string subject, string plainTextBody, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.FromAddress));
        message.To.Add(MailboxAddress.Parse(_options.ToAddress));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = plainTextBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(_options.Username, _options.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
