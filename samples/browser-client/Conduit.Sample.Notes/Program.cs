using Conduit.Generated;
using Conduit.Transport.Http;
using Conduit.Transport.SignalR;

namespace Conduit.Sample.Notes;

/// <summary>
/// Single-service sample demonstrating browser-to-server communication
/// via SignalR hub. Commands, queries, and events over a single connection.
/// </summary>
public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSingleton<NoteStore>();
        builder.Services.AddConduit(conduit =>
        {
            conduit.AddTypeFilter(filter =>
            {
                filter.AllowType("Conduit.Sample.Notes.Commands.CreateNoteCommand");
                filter.AllowType("Conduit.Sample.Notes.Commands.DeleteNoteCommand");
                filter.AllowType("Conduit.Sample.Notes.Queries.GetNotesQuery");
            });
        });
        builder.Services.AddConduitSignalR();

        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapConduitEndpoint();
        app.MapHub<ConduitHub>("/hub");
        app.Run();
    }
}
