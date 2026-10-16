using DatabaseAIAgent.Application.Abstractions.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DatabaseAIAgent.Infrastructure.Email;

internal static class MimeMessageFactory
{
    public static MimeMessage Create(EmailOptions options, EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }
}

internal sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var mime = MimeMessageFactory.Create(settings, message);
        using var client = new SmtpClient();

        var socketOptions = !settings.SmtpUseTls ? SecureSocketOptions.None
            : settings.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        await client.ConnectAsync(settings.SmtpHost!, settings.SmtpPort, socketOptions, cancellationToken);
        if (!string.IsNullOrEmpty(settings.SmtpUsername))
        {
            await client.AuthenticateAsync(settings.SmtpUsername, settings.SmtpPassword ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        // Never log message bodies: they contain single-use links.
        logger.LogInformation("Sent email '{Subject}'", message.Subject);
    }
}

internal sealed class PickupDirectoryEmailSender(IOptions<EmailOptions> options, ILogger<PickupDirectoryEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        Directory.CreateDirectory(settings.PickupDirectory);
        var path = Path.Combine(settings.PickupDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.eml");

        using var mime = MimeMessageFactory.Create(settings, message);
        await mime.WriteToAsync(path, cancellationToken);
        logger.LogInformation("Wrote email '{Subject}' to pickup directory {Path}", message.Subject, path);
    }
}
