using System.Collections.Concurrent;

namespace Sse.Server;

/// <summary>
/// Minimal in-memory token store for the auth demo. A login issues a random opaque token that is
/// returned to the caller (bearer) AND set as an HttpOnly cookie. Both map back to the same token,
/// so browser clients (cookie) and desktop clients (bearer) authenticate the same stream.
/// Not production auth — a real system would use ASP.NET Core Identity / OIDC / JWT.
/// </summary>
public sealed class AuthStore
{
    private readonly ConcurrentDictionary<string, string> _tokens = new(); // token -> user

    public string Issue(string user)
    {
        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = user;
        return token;
    }

    public bool TryGetUser(string token, out string user) => _tokens.TryGetValue(token, out user!);

    public void Revoke(string token) => _tokens.TryRemove(token, out _);
}

public static class AuthExtensions
{
    public const string CookieName = "sse_session";

    /// <summary>Pull the token from (in order) the Authorization: Bearer header, the session cookie, or ?access_token=.</summary>
    public static string? ExtractToken(this HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();

        if (ctx.Request.Cookies.TryGetValue(CookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
            return cookie;

        var q = ctx.Request.Query["access_token"].ToString();
        return string.IsNullOrEmpty(q) ? null : q;
    }
}
