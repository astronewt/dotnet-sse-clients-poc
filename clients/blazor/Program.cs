using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Sse.BlazorClient;
using Sse.ClientShared;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Release (Docker): same-origin behind the nginx edge. Debug (standalone dev): the server's http port.
#if DEBUG
var apiBase = "http://localhost:5020";
#else
var apiBase = builder.HostEnvironment.BaseAddress;
#endif
// Bearer auth via HttpClient (AUTH.md desktop path): the handler logs in and attaches the token,
// even on the /sse stream — something browser EventSource can't do.
builder.Services.AddScoped(sp =>
{
    var handler = new AuthHandler("demo") { InnerHandler = new HttpClientHandler() };
    return new HttpClient(handler) { BaseAddress = new Uri(apiBase) };
});

// Shared SSE client. WASM host must enable response streaming, or SSE never flows.
builder.Services.AddScoped(sp => new SseSensorClient(
    sp.GetRequiredService<HttpClient>(),
    request => request.SetBrowserResponseStreamingEnabled(true)));

await builder.Build().RunAsync();
