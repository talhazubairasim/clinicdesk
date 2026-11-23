using System.Collections.Concurrent;
using System.Net.Mail;

namespace ClinicDesk.Api.Infrastructure;

public record EmailMessage(string To, string Subject, string Body);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>SMTP when Smtp:Host is configured (MailHog in Docker Compose), otherwise log only.</summary>
public class EmailSender(IConfiguration config, ILogger<EmailSender> log) : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Sent.Enqueue(message);
        var host = config["Smtp:Host"];
        if (string.IsNullOrEmpty(host))
        {
            log.LogInformation("Email to {To}: {Subject}", message.To, message.Subject);
            return;
        }
        using var smtp = new SmtpClient(host, config.GetValue("Smtp:Port", 25));
        using var mail = new MailMessage(config["Smtp:From"] ?? "no-reply@clinicdesk.dev", message.To, message.Subject, message.Body);
        await smtp.SendMailAsync(mail, ct);
    }
}
