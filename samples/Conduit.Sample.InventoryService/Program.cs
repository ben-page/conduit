using Conduit.Transport.SignalR;
using Conduit.Generated;

namespace Conduit.Sample.InventoryService;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<InventoryStore>();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();
        builder.Services.AddConduitHandlers();

        var app = builder.Build();
        app.MapHub<ConduitHub>("/hub");
        app.Run();
    }
}
