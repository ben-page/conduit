# Samples

| Sample | Shows | URL |
|---|---|---|
| [`browser-client/Conduit.Sample.Notes`](browser-client/Conduit.Sample.Notes) | A browser app on the generated TypeScript `ConduitClient` over a SignalR hub: commands, queries and pushed events on one connection | https://localhost:5002 (same port as Orders; run one at a time) |
| [`service-to-service/Conduit.Sample.Inventory`](service-to-service/Conduit.Sample.Inventory) | An internal service that handles inventory commands and queries on a SignalR hub | https://localhost:5101 |
| [`service-to-service/Conduit.Sample.Orders`](service-to-service/Conduit.Sample.Orders) | A service that routes the inventory namespace to Inventory over SignalR and exposes the HTTP envelope endpoint | https://localhost:5002 |
| [`rest-api/Conduit.Sample.Todos`](rest-api/Conduit.Sample.Todos) | Commands and queries mapped to REST routes with `MapConduitGet`/`Post`/`Put`/`Delete`, OpenAPI and Swagger UI | https://localhost:49877/swagger |

The URLs come from each sample's `Properties/launchSettings.json`. Notes and Orders both use port 5002, so run one
of them at a time.

## Prerequisites

- The .NET 10 SDK, and a development certificate for HTTPS (`dotnet dev-certs https --trust`).
- Node.js 20.19 or later, with npm. That is Vite 8's minimum, and the Notes sample genuinely needs no more; working on
  the repository itself needs Node.js 22.23.4 or later (see [CONTRIBUTING.md](../CONTRIBUTING.md)). `dotnet build` at
  the repository root builds the Notes sample, whose build runs `npm install` (when `ClientApp/node_modules` is
  missing) and `vite build` (`npm run build`) in `ClientApp/`. Without Node the root build fails.

Build once at the repository root before running a sample: the Notes sample loads the TypeGen task from
`src/Conduit.TypeGen/bin/`, which the root build produces.

```shell
dotnet build
```

## Run order

Start Inventory before Orders. Orders routes `Conduit.Sample.Inventory.Commands` and
`Conduit.Sample.Inventory.Queries` to the hub at `Conduit:Endpoints:inventory:HubUri` in its `appsettings.json`
(`https://localhost:5101/conduit`), so that hub must be up when Orders dispatches to it.

```shell
dotnet run --project samples/service-to-service/Conduit.Sample.Inventory
dotnet run --project samples/service-to-service/Conduit.Sample.Orders     # in a second terminal
```

In Visual Studio, the `Orders/Inventory` profile in `Conduit.slnLaunch` starts both.

If a port is taken on your machine, pass `--urls`, and point Orders at the new Inventory address:

```shell
dotnet run --project samples/service-to-service/Conduit.Sample.Inventory --urls https://localhost:5111
Conduit__Endpoints__inventory__HubUri=https://localhost:5111/conduit \
  dotnet run --project samples/service-to-service/Conduit.Sample.Orders --urls https://localhost:5012
```

The other two samples run on their own:

```shell
dotnet run --project samples/browser-client/Conduit.Sample.Notes   # open https://localhost:5002
dotnet run --project samples/rest-api/Conduit.Sample.Todos         # open https://localhost:49877/swagger
```

## Calling the envelope endpoint

Orders maps the HTTP envelope endpoint with `app.MapConduitEndpoint()`: `POST /api/conduit/dispatch` for commands and
queries, `POST /api/conduit/publish` for events. The message's full type name goes in the `X-Conduit-Type` header and
the body is the message itself, serialized as camelCase JSON.

```shell
curl -k https://localhost:5002/api/conduit/dispatch \
  -H "Content-Type: application/json" \
  -H "X-Conduit-Type: Conduit.Sample.Orders.Commands.CreateOrderCommand" \
  -d '{"productId":"WIDGET-001","quantity":2}'
```

Orders handles `CreateOrderCommand` itself, and its handler sends `ReserveInventoryCommand` to Inventory over SignalR.
The answer is a `DispatchResult` envelope, `data` on success and `error` on a refusal:

```json
{"data":{"orderId":"99a37c3c-d358-4da9-90f6-388a842d34b4","inventoryReserved":true},"error":null}
```

A message Orders has no handler for but routes is forwarded as it arrived. This query reaches Inventory's handler
and answers the stock left (`{"data":98,"error":null}` after the order above):

```shell
curl -k https://localhost:5002/api/conduit/dispatch \
  -H "Content-Type: application/json" \
  -H "X-Conduit-Type: Conduit.Sample.Inventory.Queries.CheckStockQuery" \
  -d '{"productId":"WIDGET-001"}'
```

## Using the packages

The samples reference Conduit's projects under `src/` so that they build against the code in this repository. In your
own project, reference the packages instead:

```shell
dotnet add package Conduit.NET
dotnet add package Conduit.NET.SourceGen
dotnet add package Conduit.NET.Transport.SignalR   # Notes, Inventory, Orders
dotnet add package Conduit.NET.Transport.Http      # Notes, Orders, Todos
dotnet add package Conduit.NET.TypeGen             # Notes: the generated TypeScript client
```
