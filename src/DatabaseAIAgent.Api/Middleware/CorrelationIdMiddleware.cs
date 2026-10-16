using System.Diagnostics;
using Serilog.Context;

namespace DatabaseAIAgent.Api.Middleware;

/// <summary>
/// Ensures every request has a correlation id: accepts a well-formed inbound <c>X-Correlation-ID</c>,
/// otherwise generates one. The id is echoed in the response, pushed into the log context and attached
/// to the current trace so user-facing errors can be matched with internal logs.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context.Request.Headers[HeaderName].ToString());
        context.Items[ItemKey] = correlationId;
        context.TraceIdentifier = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(ItemKey, correlationId))
        {
            await next(context);
        }
    }

    public static string Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;

    // Only accept short, header/log-safe ids so a client cannot inject arbitrary content into logs.
    internal static string Resolve(string? inbound) =>
        !string.IsNullOrEmpty(inbound) && inbound.Length <= MaxLength && inbound.All(IsAllowed)
            ? inbound
            : Guid.NewGuid().ToString("N");

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
}
