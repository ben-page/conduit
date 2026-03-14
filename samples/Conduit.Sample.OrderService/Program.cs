using Conduit.Bridge.Http;
using Conduit.Bridge.SignalR;
using Conduit.Transport.SignalR;
using Conduit.Generated;

namespace Conduit.Sample.OrderService;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddConduit(conduit =>
        {
            // Route InventoryService commands to the remote service
            conduit.AddEndpoint("inventory", endpoint =>
                endpoint.UseSignalR(new Uri("http://localhost:5101/hub")));
            conduit.Route("Conduit.Sample.InventoryService").To("inventory");
        });
        builder.Services.AddConduitSignalR();
        builder.Services.AddConduitBridge(bridge =>
        {
            bridge.AllowType("Conduit.Sample.OrderService.Commands.CreateItemCommand");
            bridge.AllowType("Conduit.Sample.OrderService.Commands.DeleteItemCommand");
            bridge.AllowType("Conduit.Sample.OrderService.Queries.GetItemQuery");
        });
        builder.Services.AddConduitHandlers();

        var app = builder.Build();

        app.UseStaticFiles();
        app.MapConduitBridge();
        app.MapHub<ConduitHub>("/hub");
        app.Run();
    }
}
