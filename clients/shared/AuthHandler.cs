using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Sse.ClientShared;

/// <summary>
/// Desktop/Blazor auth: a DelegatingHandler that logs in for a bearer token, attaches it to every
/// request (including the /sse stream — impossible with browser EventSource), and clears it on 401
/// so the next request re-logs-in. Because the SSE client reconnects on failure, that reconnect
/// transparently re-authenticates. This is the HttpClient path from AUTH.md.
/// </summary>
public sealed class AuthHandler(string password) : DelegatingHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal))
            return await base.SendAsync(request, ct);

        await EnsureTokenAsync(request.RequestUri, ct);
        if (_token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            _token = null;   // expired/revoked -> next request (or reconnect) re-authenticates
        return response;
    }

    private async Task EnsureTokenAsync(Uri reference, CancellationToken ct)
    {
        if (_token is not null) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null) return;
            var loginUri = new Uri(reference, $"/auth/login?password={Uri.EscapeDataString(password)}");
            using var loginReq = new HttpRequestMessage(HttpMethod.Post, loginUri);
            using var resp = await base.SendAsync(loginReq, ct);
            if (resp.IsSuccessStatusCode)
            {
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                var body = await JsonSerializer.DeserializeAsync<LoginResponse>(s, Json, ct);
                _token = body?.Token;
            }
        }
        finally { _gate.Release(); }
    }

    private sealed record LoginResponse(bool Authenticated, string? User, string? Token);
}
