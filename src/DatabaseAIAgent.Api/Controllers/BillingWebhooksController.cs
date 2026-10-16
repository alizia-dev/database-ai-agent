using System.Text;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Billing.Webhooks;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Controllers;

/// <summary>
/// Payment-provider webhooks. Authenticity comes from the provider signature, verified over the exact raw body, so
/// these endpoints are anonymous. Returning non-2xx on processing failure makes the provider redeliver; duplicates
/// are recognized by event ID and never applied twice.
/// </summary>
[ApiController]
[Route("api/billing/webhooks")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class BillingWebhooksController(BillingWebhookService webhooks) : ControllerBase
{
    [HttpPost("stripe")]
    public Task<IActionResult> Stripe(CancellationToken cancellationToken) => ReceiveAsync(PaymentProvider.Stripe, cancellationToken);

    [HttpPost("paypal")]
    public Task<IActionResult> PayPal(CancellationToken cancellationToken) => ReceiveAsync(PaymentProvider.PayPal, cancellationToken);

    private async Task<IActionResult> ReceiveAsync(PaymentProvider provider, CancellationToken cancellationToken)
    {
        string payload;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            payload = await reader.ReadToEndAsync(cancellationToken);
        }

        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var outcome = await webhooks.ReceiveAsync(provider, new WebhookRequest(payload, headers), cancellationToken);

        return outcome switch
        {
            WebhookOutcome.Processed or WebhookOutcome.Ignored or WebhookOutcome.Duplicate => Ok(),
            WebhookOutcome.InvalidSignature => BadRequest(),
            WebhookOutcome.ProviderNotEnabled => NotFound(),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
