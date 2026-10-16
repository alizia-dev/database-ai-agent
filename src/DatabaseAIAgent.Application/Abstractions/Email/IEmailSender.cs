namespace DatabaseAIAgent.Application.Abstractions.Email;

public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

/// <summary>Sends transactional email (verification, password reset, invitations).</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
