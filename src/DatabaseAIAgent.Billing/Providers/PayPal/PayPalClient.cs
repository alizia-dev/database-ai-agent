using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DatabaseAIAgent.Application.Abstractions.Billing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Billing.Providers.PayPal;

/// <summary>Minimal PayPal REST client: OAuth client-credentials token (cached) and JSON calls.</summary>
internal sealed class PayPalClient(HttpClient http, IOptions<PayPalOptions> options, TimeProvider time, ILogger<PayPalClient> logger)
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static (string Token, DateTimeOffset ExpiresAt)? _token;

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(options.Value.ApiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(cancellationToken));
        request.Headers.Add("PayPal-Request-Id", Guid.NewGuid().ToString("N"));
        if (body is not null)
        {
            request.Content = body is JsonNode node
                ? new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json")
                : JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // debug_id lets PayPal support trace the call; the body may contain customer data, so it is not logged.
            var debugId = TryParse(text)?["debug_id"]?.GetValue<string>();
            logger.LogError("PayPal {Method} {Path} failed with {StatusCode} (debug_id {DebugId})", method, path, (int)response.StatusCode, debugId);
            throw new PaymentProviderException("The payment provider could not complete the request. Please try again in a moment.");
        }

        return string.IsNullOrWhiteSpace(text) ? null : TryParse(text);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached && cached.ExpiresAt > time.GetUtcNow())
        {
            return cached.Token;
        }

        await TokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } fresh && fresh.ExpiresAt > time.GetUtcNow())
            {
                return fresh.Token;
            }

            var settings = options.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.ApiBase, "v1/oauth2/token"))
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}")));

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("PayPal authentication failed with {StatusCode}", (int)response.StatusCode);
                throw new PaymentProviderException("The payment provider is not available right now. Please try again later.");
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var token = json.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 300;
            _token = (token, time.GetUtcNow().AddSeconds(Math.Max(expiresIn - 60, 30)));
            return token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Test hook: clears the cached token.</summary>
    internal static void ResetTokenCache() => _token = null;
}
