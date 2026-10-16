using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Stripe;

namespace DatabaseAIAgent.Api.Tests.Billing;

/// <summary>
/// Stands in for api.stripe.com at the HTTP layer, so the real Stripe.net client and provider code run unchanged.
/// Records every request (method, path, form body) for assertions.
/// </summary>
public sealed class FakeStripeApi : HttpMessageHandler
{
    public const string CustomerId = "cus_test_123";
    public const string SubscriptionId = "sub_test_1";

    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public sealed record RecordedRequest(string Method, string Path, string Body);

    public static StripeClient CreateClient(FakeStripeApi handler) =>
        new("sk_test_fake", httpClient: new SystemNetHttpClient(new HttpClient(handler), maxNetworkRetries: 0));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : Uri.UnescapeDataString(await request.Content.ReadAsStringAsync(cancellationToken));
        var path = request.RequestUri!.AbsolutePath;
        _requests.Enqueue(new RecordedRequest(request.Method.Method, path, body));

        var json = (request.Method.Method, path) switch
        {
            ("POST", "/v1/customers") => $$"""{"id":"{{CustomerId}}","object":"customer"}""",
            ("POST", "/v1/checkout/sessions") => """{"id":"cs_test_1","object":"checkout.session","mode":"subscription","url":"https://checkout.stripe.com/c/pay/cs_test_1"}""",
            ("POST", "/v1/billing_portal/sessions") => """{"id":"bps_test_1","object":"billing_portal.session","url":"https://billing.stripe.com/p/session/test_1"}""",
            ("GET", var p) when p.StartsWith("/v1/subscriptions/", StringComparison.Ordinal) => SubscriptionJson("price_pro_monthly"),
            ("POST", var p) when p.StartsWith("/v1/subscriptions/", StringComparison.Ordinal) => SubscriptionJson("price_business_monthly"),
            ("DELETE", var p) when p.StartsWith("/v1/subscriptions/", StringComparison.Ordinal) => SubscriptionJson("price_pro_monthly", "canceled"),
            _ => null,
        };

        return json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"type":"invalid_request_error","message":"Unknown test route"}}""", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static string SubscriptionJson(string priceId, string status = "active") => JsonSerializer.Serialize(new
    {
        id = SubscriptionId,
        @object = "subscription",
        customer = CustomerId,
        status,
        cancel_at_period_end = false,
        items = new
        {
            @object = "list",
            data = new[]
            {
                new { id = "si_test_1", @object = "subscription_item", current_period_start = 1767225600, current_period_end = 1769904000, price = new { id = priceId, @object = "price" } },
            },
        },
    });
}
