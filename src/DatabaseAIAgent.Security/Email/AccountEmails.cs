using System.Net;
using DatabaseAIAgent.Application.Abstractions.Email;
using DatabaseAIAgent.Application.Common;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Security.Email;

/// <summary>Builds account emails. All interpolated values are HTML-encoded; links point at the configured web app.</summary>
public sealed class AccountEmails(IOptions<AppUrlOptions> appUrl)
{
    public EmailMessage VerifyEmail(string to, string displayName, Guid userId, string encodedToken) =>
        Compose(
            to,
            "Verify your email address",
            $"Hi {displayName},",
            "Confirm your email address to finish setting up your Database AI Agent account.",
            "Verify email",
            Link("verify-email", ("userId", userId.ToString()), ("token", encodedToken)),
            "If you did not create an account, you can ignore this email.");

    public EmailMessage AccountAlreadyExists(string to) =>
        Compose(
            to,
            "Sign-in help for your account",
            "Hello,",
            "Someone tried to register with this email address, but an account already exists. If this was you, sign in or reset your password.",
            "Reset password",
            Link("forgot-password"),
            "If this was not you, no action is needed. Your account has not been changed.");

    public EmailMessage ResetPassword(string to, string displayName, string encodedToken) =>
        Compose(
            to,
            "Reset your password",
            $"Hi {displayName},",
            "We received a request to reset your password. This link expires soon and can be used once.",
            "Choose a new password",
            Link("reset-password", ("email", to), ("token", encodedToken)),
            "If you did not request this, ignore this email. Your password will not change.");

    public EmailMessage Invitation(string to, string workspaceName, string inviterName, string role, string token) =>
        Compose(
            to,
            $"You're invited to {workspaceName}",
            "Hello,",
            $"{inviterName} invited you to join the {workspaceName} workspace on Database AI Agent as {role}.",
            "Accept invitation",
            Link("accept-invite", ("token", token)),
            "If you were not expecting this invitation, you can ignore this email.");

    public EmailMessage PasswordChanged(string to, string displayName) =>
        Compose(
            to,
            "Your password was changed",
            $"Hi {displayName},",
            "The password for your Database AI Agent account was just changed and all other sessions were signed out.",
            "Sign in",
            Link("login"),
            "If you did not make this change, reset your password immediately and contact your administrator.");

    private string Link(string path, params (string Key, string Value)[] query)
    {
        var baseUrl = appUrl.Value.PublicUrl.TrimEnd('/');
        var queryString = query.Length == 0
            ? string.Empty
            : "?" + string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        return $"{baseUrl}/{path}{queryString}";
    }

    private static EmailMessage Compose(string to, string subject, string greeting, string body, string action, string url, string footer)
    {
        var text = $"{greeting}\n\n{body}\n\n{action}: {url}\n\n{footer}\n";
        var html = $"""
            <!doctype html>
            <html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2933;line-height:1.5">
            <p>{Encode(greeting)}</p>
            <p>{Encode(body)}</p>
            <p><a href="{Encode(url)}" style="display:inline-block;padding:10px 18px;background:#1d4ed8;color:#fff;text-decoration:none;border-radius:6px">{Encode(action)}</a></p>
            <p style="font-size:13px;color:#52606d">Or paste this link into your browser:<br>{Encode(url)}</p>
            <p style="font-size:13px;color:#52606d">{Encode(footer)}</p>
            </body></html>
            """;
        return new EmailMessage(to, subject, text, html);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
