namespace DatabaseAIAgent.Api.Authentication;

/// <summary>
/// The refresh token lives only in an httpOnly, Secure, SameSite=Strict cookie scoped to the auth endpoints, so
/// scripts cannot read it and other sites cannot send it. The short-lived access token is kept in memory by the client.
/// </summary>
internal static class RefreshTokenCookie
{
    public const string Name = "__Secure-daa_rt";
    public const string Path = "/api/auth";

    /// <summary>Custom header required on cookie-authenticated endpoints; cross-site forms cannot set it (CSRF defence in depth).</summary>
    public const string CsrfHeader = "X-Requested-With";

    public static void Set(HttpResponse response, string token, DateTimeOffset expiresAt) =>
        response.Cookies.Append(Name, token, Options(expiresAt));

    public static void Clear(HttpResponse response) =>
        response.Cookies.Delete(Name, Options(DateTimeOffset.UnixEpoch));

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    public static bool HasCsrfHeader(HttpRequest request) => !string.IsNullOrEmpty(request.Headers[CsrfHeader]);

    private static CookieOptions Options(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expiresAt,
        IsEssential = true,
    };
}
