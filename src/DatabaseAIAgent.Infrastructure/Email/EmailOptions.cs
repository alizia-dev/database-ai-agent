using System.ComponentModel.DataAnnotations;

namespace DatabaseAIAgent.Infrastructure.Email;

public enum EmailTransport
{
    /// <summary>Deliver through an SMTP relay (production; Mailpit in local docker-compose).</summary>
    Smtp = 0,

    /// <summary>Write .eml files to a directory instead of sending. Development only.</summary>
    PickupDirectory = 1,
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public EmailTransport Transport { get; set; } = EmailTransport.Smtp;

    [Required, EmailAddress]
    public string FromAddress { get; set; } = string.Empty;

    [Required]
    public string FromName { get; set; } = "Database AI Agent";

    public string? SmtpHost { get; set; }

    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 587;

    /// <summary>Use STARTTLS (port 587) or implicit TLS (port 465). Disable only for a local SMTP catcher.</summary>
    public bool SmtpUseTls { get; set; } = true;

    public string? SmtpUsername { get; set; }

    public string? SmtpPassword { get; set; }

    public string PickupDirectory { get; set; } = "mail-pickup";
}
