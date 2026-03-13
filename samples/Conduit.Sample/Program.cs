using Conduit.Transport.AspNetCore;
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
        builder.Services.AddConduitHandlers();

        var app = builder.Build();
        app.MapConduit();
        app.MapHub<ConduitHub>("/hub");
        app.Run();
    }
}
