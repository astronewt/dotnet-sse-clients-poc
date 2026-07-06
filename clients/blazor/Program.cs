using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Crw.BlazorClient;
using Crw.ClientShared;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Same-origin: behind the nginx edge, "/sse" and "/admin" resolve to the server.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Shared SSE client. WASM host must enable response streaming, or SSE never flows.
builder.Services.AddScoped(sp => new SseSensorClient(
    sp.GetRequiredService<HttpClient>(),
    request => request.SetBrowserResponseStreamingEnabled(true)));

await builder.Build().RunAsync();
