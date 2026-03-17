# Cross-Service Routing

Conduit routes commands and queries to remote services transparently — the calling code uses `IMediator` identically to local dispatch. Configure where messages go, and the generated mediator handles serialization, transport, and deserialization. The handler's location becomes a deployment concern, not a code concern.

This walkthrough uses two services from the sample apps: an Orders service that sends commands, and an Inventory service that handles them.

## Shared contracts library

Put message types in a standalone class library. Both services reference it. The contracts project references only `Conduit` (core) -- no ASP.NET, no transports.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Conduit" />
  </ItemGroup>
</Project>
```

The contracts are plain records implementing `ICommand` or `IQuery`:

```csharp
using Conduit.Messages;

namespace Conduit.Sample.Inventory.Commands;

public record ReserveInventoryCommand(string ProductId, int Quantity) : ICommand<bool>;
```

```csharp
using Conduit.Messages;

namespace Conduit.Sample.Inventory.Queries;

public record CheckStockQuery(string ProductId) : IQuery<int>;
```

The namespace matters. Routes match by namespace prefix, so keep contract types in a predictable namespace hierarchy.

## Handling service (Inventory)

The Inventory service references the contracts library, implements handlers, and exposes a SignalR hub for inbound dispatch.

```csharp
using Conduit.Handlers;
using Conduit.Sample.Inventory.Commands;

internal sealed class ReserveInventoryHandler(InventoryStore store)
    : ICommandHandler<ReserveInventoryCommand, bool>
{
    public Task<bool> HandleAsync(
        ReserveInventoryCommand command,
        CancellationToken cancellationToken)
    {
        var success = store.TryReserve(command.ProductId, command.Quantity);
        return Task.FromResult(success);
    }
}
```

```csharp
using Conduit.Handlers;
using Conduit.Sample.Inventory.Queries;

internal sealed class CheckStockHandler(InventoryStore store)
    : IQueryHandler<CheckStockQuery, int>
{
    public Task<int> HandleAsync(
        CheckStockQuery query,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(store.GetStock(query.ProductId));
    }
}
```

The Inventory `Program.cs` registers Conduit, adds SignalR support, and maps the hub:

```csharp
using Conduit.Generated;
using Conduit.Transport.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<InventoryStore>();
builder.Services.AddConduit();
builder.Services.AddConduitSignalR();

var app = builder.Build();
app.MapHub<ConduitHub>("/hub");
app.Run();
```

`AddConduit()` registers the handlers the source generator discovered. `AddConduitSignalR()` registers the SignalR transport services. `MapHub<ConduitHub>("/hub")` exposes the inbound endpoint that other services connect to.

## Sending service (Orders)

The Orders service references the same contracts library but has no handlers for those types. It routes them to the Inventory service instead.

```csharp
using Conduit.Attributes;
using Conduit.Generated;
using Conduit.Transport.Http;
using Conduit.Transport.SignalR;

[assembly: RemoteContracts("Conduit.Sample.Inventory")]

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
```

Three things happen here:

1. `AddEndpoint("inventory", ...)` defines a named endpoint and configures it to connect via SignalR.
2. `Route("Conduit.Sample.Inventory").To("inventory")` tells the generated mediator to send any command or query in the `Conduit.Sample.Inventory` namespace (or its children) to the `"inventory"` endpoint.
3. `[assembly: RemoteContracts("Conduit.Sample.Inventory")]` tells the source generator these types are handled remotely (see below).

## Dispatching

From the Orders service, dispatching a remote command looks identical to local dispatch:

```csharp
bool reserved = await mediator.ExecuteAsync(
    new ReserveInventoryCommand("SKU-42", 5),
    cancellationToken);
```

The generated `ConduitMediator` checks the route table before the local type switch. `ReserveInventoryCommand` lives in `Conduit.Sample.Inventory.Commands`, which matches the route pattern `Conduit.Sample.Inventory`. The mediator wraps the command in a `DispatchEnvelope`, sends it to the Inventory service via SignalR, and deserializes the response. The calling code never knows the handler is in another process.

## `[RemoteContracts]`

The source generator produces a `CONDUIT001` warning for any command or query type that has no local handler. This is useful -- it catches missing handlers -- but in the sending service, there are no local handlers by design. The contracts exist only to be routed.

```csharp
[assembly: RemoteContracts("Conduit.Sample.Inventory")]
```

This assembly-level attribute suppresses `CONDUIT001` for all types whose namespace starts with `Conduit.Sample.Inventory`. It accepts `AllowMultiple = true`, so add one per remote namespace if you route to multiple services:

```csharp
[assembly: RemoteContracts("Conduit.Sample.Inventory")]
[assembly: RemoteContracts("Conduit.Sample.Billing")]
```

The attribute only affects diagnostics. It does not change routing behavior -- routes are configured separately via `Route().To()`.

## Transport choice

Conduit supports two outbound transports. Choose based on what the remote service exposes.

**SignalR** -- bidirectional. Commands, queries, and events flow in both directions. The Inventory sample uses this:

```csharp
conduit.AddEndpoint("inventory", endpoint =>
    endpoint.UseSignalR(new Uri("https://localhost:5101/hub")));
```

**HTTP** -- request/response only. Commands and queries work, but there is no server-to-client push. Events cannot flow from the remote service back to the caller:

```csharp
conduit.AddEndpoint("inventory", endpoint =>
    endpoint.UseHttp(httpClient));
```

Use SignalR when you need event push from the remote service. Use HTTP when you just need to send commands and queries and the remote service already exposes `MapConduitEndpoint()`.

## Ownership model

| Message Type | Defined In | Routed By |
|---|---|---|
| Commands/Queries | Contracts library | Sending service configures `Route().To()` |
| Events | Publishing service | N/A -- events broadcast, not routed |

Commands and queries are owned by the handling service. The contracts library is that service's public API surface. Other services reference it and configure routes.

Events are owned by the publishing service. They broadcast to all registered `IConduitTransport` instances -- there is no route table for events. Services that care about an event register a listener locally and receive it via whatever transport connects them.

Next: [Pipeline Behaviors](pipeline-behaviors.md)
