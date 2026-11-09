using Conduit.Generated;
using Conduit.Transport.SignalR;

namespace Conduit.Sample.Inventory;

/// <summary>
/// Internal inventory service. Exposes a SignalR hub for receiving
/// commands from other services (e.g. OrderService).
/// </summary>
public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSingleton<InventoryStore>();
        builder.Services.AddConduit();
        // Only other services (OrderService) reach this hub, never a browser, so it keeps their correlation ids and the
        // conversation spans both services. A browser-facing hub leaves TrustCallerContext off (the default).
        builder.Services.AddConduitSignalR(options => options.TrustCallerContext = true);

        var app = builder.Build();
        app.MapHub<ConduitHub>("/conduit");
        app.Run();
    }
}
