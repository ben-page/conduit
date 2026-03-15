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

        builder.Services.AddConduit(conduit =>
        {
            conduit.AddEndpoint("inventory", endpoint =>
                endpoint.UseSignalR(new Uri("https://localhost:5101/hub")));
            conduit.Route("Conduit.Sample.Inventory").To("inventory");
        });

        var app = builder.Build();
        app.MapConduitEndpoint();
        app.Run();
    }
}
