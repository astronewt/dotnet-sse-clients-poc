using System.Net.Http;
using System.Windows;
using Sse.ClientShared;
using Microsoft.Extensions.DependencyInjection;

namespace Sse.WpfClient;

/// <summary>
/// Interaction logic for MainWindow.xaml. Wires the DI container the BlazorWebView uses.
/// The desktop host talks to the server directly with a native HttpClient — no CORS,
/// streams by default (no SetBrowserResponseStreamingEnabled), HTTP/2 for free.
/// </summary>
public partial class MainWindow : Window
{
    // Server's http endpoint. Change if the server runs elsewhere.
    private const string ServerBaseUrl = "http://localhost:5020";

    public MainWindow()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(ServerBaseUrl) });
        // No request configurator: native HttpClient already streams the response.
        services.AddScoped(sp => new SseSensorClient(sp.GetRequiredService<HttpClient>()));

        blazor.Services = services.BuildServiceProvider();
    }
}
