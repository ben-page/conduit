using Conduit.Attributes;
using Conduit.Generated;
using Conduit.Transport.Http;
using Conduit.Transport.SignalR;

[assembly: RemoteContracts("Conduit.Sample.Inventory")]

namespace Conduit.Sample.Orders;

/// <summary>
/// Order service demonstrating cross-service communication.
/// Routes inventory commands to InventoryService via SignalR.
/// Exposes HTTP dispatch endpoint for curl/Postman testing.
/// </summary>
public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddConduit(builder.Configuration, conduit =>
        {
            conduit.AddEndpoint("inventory", endpoint =>
            {
                endpoint.UseSignalR();
                endpoint.RouteNamespace<Inventory.Commands.ReserveInventoryCommand>();
                endpoint.RouteNamespace<Inventory.Queries.CheckStockQuery>();
            });
        });

        var app = builder.Build();
        app.MapConduitEndpoint();
        app.Run();
    }
}
