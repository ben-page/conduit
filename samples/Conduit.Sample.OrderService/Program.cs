using Conduit.Generated;
using Conduit.Transport.Http;
using Conduit.Transport.SignalR;

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
            conduit.AddTypeFilter(filter =>
            {
                filter.AllowType("Conduit.Sample.OrderService.Commands.CreateItemCommand");
                filter.AllowType("Conduit.Sample.OrderService.Commands.DeleteItemCommand");
                filter.AllowType("Conduit.Sample.OrderService.Queries.GetItemQuery");
            });
        });
        builder.Services.AddConduitSignalR();

        var app = builder.Build();

        app.UseStaticFiles();
        app.MapConduitEndpoint();
        app.MapHub<ConduitHub>("/hub");
        app.Run();
    }
}
