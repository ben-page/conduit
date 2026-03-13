using global::Conduit;
using Conduit.Bridge.Http;
using Conduit.Bridge.SignalR;
using Conduit.Transport.SignalR;
using Conduit.Generated;

namespace Conduit.Sample;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();
        builder.Services.AddConduitBridge(bridge =>
        {
            bridge.AllowType("CreateItemCommand");
            bridge.AllowType("DeleteItemCommand");
            bridge.AllowType("GetItemQuery");
        });
        builder.Services.AddConduitHandlers();

        var app = builder.Build();
        app.MapConduitBridge();
        app.MapHub<ConduitHub>("/hub");
        app.Run();
    }
}
