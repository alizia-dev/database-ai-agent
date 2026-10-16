using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DatabaseAIAgent.Billing.Webhooks;

/// <summary>
/// Background worker that re-applies webhook events which were stored but failed (or were interrupted), up to
/// <see cref="Domain.Billing.WebhookEvent.MaxAttempts"/> times. Runs in the Workers host.
/// </summary>
public sealed class WebhookRetryService(IServiceScopeFactory scopeFactory, IClock clock, ILogger<WebhookRetryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RetryBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Webhook retry batch failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> RetryBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IWebhookEventRepository>();
        var webhooks = scope.ServiceProvider.GetRequiredService<BillingWebhookService>();

        var due = await events.ListRetryableAsync(clock.UtcNow - MinimumAge, take: 50, cancellationToken);
        foreach (var webhookEvent in due)
        {
            await webhooks.ReprocessAsync(webhookEvent, cancellationToken);
        }

        if (due.Count > 0)
        {
            logger.LogInformation("Retried {Count} billing webhook events", due.Count);
        }

        return due.Count;
    }
}
