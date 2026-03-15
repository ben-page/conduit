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
        builder.Services.AddConduitSignalR();

        var app = builder.Build();
        app.MapHub<ConduitHub>("/hub");
        app.Run();
    }
}
