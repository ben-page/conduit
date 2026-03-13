using Conduit.AspNetCore;
using Conduit.Generated;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddConduit();
builder.Services.AddConduitHandlers();

var app = builder.Build();
app.MapConduit();
app.MapHub<ConduitHub>("/hub");
app.Run();
