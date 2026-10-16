using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.Extensions.Logging;

namespace DatabaseAIAgent.Billing.Webhooks;

public enum WebhookOutcome
{
    Processed = 0,
    Ignored = 1,
    Duplicate = 2,
    InvalidSignature = 3,
    ProviderNotEnabled = 4,
    Failed = 5,
}

/// <summary>
/// Receives provider webhooks: verify signature, store once (idempotency by provider event ID), then apply.
/// Subscription access changes only ever happen here, never on a browser redirect. Events that fail to apply are
/// kept and retried by the worker (and by the provider's own redelivery).
/// </summary>
public sealed class BillingWebhookService(
    IEnumerable<IPaymentProvider> providers,
    IWebhookEventRepository events,
    IUnitOfWork unitOfWork,
    IClock clock,
    BillingEventApplier applier,
    ILogger<BillingWebhookService> logger)
{
    public async Task<WebhookOutcome> ReceiveAsync(PaymentProvider provider, WebhookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gateway = providers.SingleOrDefault(p => p.Kind == provider);
        if (gateway is not { IsEnabled: true })
        {
            return WebhookOutcome.ProviderNotEnabled;
        }

        var verified = await gateway.VerifyWebhookAsync(request, cancellationToken);
        if (verified is null)
        {
            logger.LogWarning("Rejected {Provider} webhook with an invalid signature", provider);
            return WebhookOutcome.InvalidSignature;
        }

        var existing = await events.FindAsync(provider, verified.EventId, cancellationToken);
        if (existing is not null)
        {
            return existing is { Status: WebhookEventStatus.Failed, IsRetryable: true }
                ? await ProcessAsync(gateway, existing, cancellationToken)
                : WebhookOutcome.Duplicate;
        }

        var webhookEvent = new WebhookEvent(provider, verified.EventId, verified.Type, request.Payload, clock.UtcNow);
        if (!await events.TryAddAsync(webhookEvent, cancellationToken))
        {
            return WebhookOutcome.Duplicate;
        }

        return await ProcessAsync(gateway, webhookEvent, cancellationToken);
    }

    /// <summary>Re-applies a stored event that previously failed (called by the retry worker).</summary>
    public Task<WebhookOutcome> ReprocessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhookEvent);
        var gateway = providers.Single(p => p.Kind == webhookEvent.Provider);
        return ProcessAsync(gateway, webhookEvent, cancellationToken);
    }

    private async Task<WebhookOutcome> ProcessAsync(IPaymentProvider gateway, WebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        try
        {
            var billingEvent = gateway.Interpret(webhookEvent.Payload);
            var outcome = await applier.ApplyAsync(gateway.Kind, billingEvent, cancellationToken);
            webhookEvent.MarkProcessed(clock.UtcNow, ignored: outcome == ApplyOutcome.Ignored);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return outcome == ApplyOutcome.Ignored ? WebhookOutcome.Ignored : WebhookOutcome.Processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to apply {Provider} webhook {EventId} ({Type})", gateway.Kind, webhookEvent.ProviderEventId, webhookEvent.Type);

            // Never persist half-applied changes: drop them, then record the failure on a fresh copy of the event.
            unitOfWork.DiscardChanges();
            var stored = await events.FindAsync(gateway.Kind, webhookEvent.ProviderEventId, cancellationToken);
            if (stored is not null)
            {
                stored.MarkFailed(ex is BillingConfigurationException ? ex.Message : ex.GetType().Name);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return WebhookOutcome.Failed;
        }
    }
}
