# Conduit

A source-generated mediator for .NET 10, with a typed TypeScript client and transports for HTTP, SignalR and four
message brokers. Commands, queries and events are plain C# records; the mediator, its dispatch code and its DI
registration are emitted at compile time, so finding and calling a handler needs no reflection and no assembly
scanning.

Documentation: <https://ben-page.github.io/conduit/>

## Why Conduit

Your front end and your back end are one application. The usual way to join them is an API designed for strangers: a
REST surface whose resource model was shaped for no screen in particular, DTOs and the mapping between them, and
business rules that drift into the browser because a resource update is naturally a state diff. With CQRS, where every
read model is bespoke, that boundary decouples nothing.

Conduit lets the front end call the application directly. A screen is a query with a read model of its own. A button
is a command that names the user's intent, and the rules stay in its handler. The browser dispatches both through a
generated, typed `ConduitClient` to the same `IQueryHandler` and `ICommandHandler` your C# code reaches and, over
SignalR, receives events on the same connection. When a consumer really is a stranger, map the same handler to a REST
route. When a handler moves to another service, route its messages there; the handler does not change.

The argument in full, with examples: [Why Conduit](https://ben-page.github.io/conduit/).

## Packages

Every package id starts `Conduit.NET`; the namespaces and assemblies inside are `Conduit` and `Conduit.*`.

| Package | What it does |
|---|---|
| `Conduit.NET` | The runtime: message and handler interfaces, routing, the dispatch pipeline, configuration |
| `Conduit.NET.SourceGen` | The source generator: emits the mediator and `AddConduit()` into the host project |
| `Conduit.NET.Contracts` | Message interfaces, attributes and exceptions with no package dependencies, for contract and domain libraries (`Conduit.NET` brings it in) |
| `Conduit.NET.TypeGen` | An MSBuild task that writes the typed TypeScript client, with its HTTP and SignalR transports |
| `Conduit.NET.Transport.Http` | Envelope and REST endpoints that receive messages; a client that routes commands and queries to another service |
| `Conduit.NET.Transport.SignalR` | A hub that serves browsers and services and pushes events to browsers; a client that routes to another service's hub |
| `Conduit.NET.Transport.AzureServiceBus` | Azure Service Bus: a topic and its subscriptions |
| `Conduit.NET.Transport.RabbitMQ` | RabbitMQ: exchanges and queues |
| `Conduit.NET.Transport.AmazonSqs` | Amazon SQS queues, with SNS topics for fan-out |
| `Conduit.NET.Transport.ActiveMQ` | ActiveMQ Artemis over AMQP 1.0 |
| `Conduit.NET.Durability` | A transactional outbox, an idempotent inbox and a dead-letter queue |
| `Conduit.NET.Jobs` | Cron and interval schedules and event triggers that run commands, once across the cluster when a database engine is registered |
| `Conduit.NET.Audit` | An audit trail of every dispatch |
| `Conduit.NET.Resilience` | Polly retry and circuit-breaker pipelines around each dispatch |
| `Conduit.NET.Persistence` | The storage abstraction that Durability, Audit and Jobs share |
| `Conduit.NET.Persistence.Sqlite`, `Conduit.NET.Persistence.Postgres`, `Conduit.NET.Persistence.SqlServer`, `Conduit.NET.Persistence.MySql` | The database engines; reference one |

## Install

```shell
dotnet add package Conduit.NET
dotnet add package Conduit.NET.SourceGen
```

`Conduit.NET.SourceGen` must be referenced explicitly, from the host project. The generator deliberately does not ride
along with the `Conduit.NET` runtime package: a library that references `Conduit.NET` for its messages and handlers runs
no generator, and the one host that composes them emits the single mediator for the process (see
[Modular Monolith](https://ben-page.github.io/conduit/modular-monolith/)).

Add a transport package for each way messages enter or leave the host, and `Conduit.NET.TypeGen` for a browser client:

```shell
dotnet add package Conduit.NET.Transport.SignalR    # or .Http, .AzureServiceBus, .RabbitMQ, .AmazonSqs, .ActiveMQ
dotnet add package Conduit.NET.TypeGen
```

`Conduit.NET.TypeGen` runs after each build of a project that sets
`<ConduitTypeGenEnabled>true</ConduitTypeGenEnabled>`, and writes `conduit.ts` and `conduit.generated.ts` (to
`ClientApp/generated` by default). Their SignalR transport imports the `@microsoft/signalr` npm package. Build with
the `dotnet` CLI: Visual Studio's own `msbuild.exe` cannot load the task. See
[TypeScript Client](https://ben-page.github.io/conduit/transports/typescript-client/).

On a broker transport, Conduit dispatches each delivery once and never retries it itself. A failed request is answered
to its caller, and an unexpected failure (not a refusal) is also captured to the
[dead-letter queue](https://ben-page.github.io/conduit/dead-letter-queue/) when one is configured. A failed event or
fire-and-forget command is captured there too or, without one, handed back to the broker, whose own redelivery and
dead-letter settings apply. Retrying is opt-in, around the handler:
[Dispatch Retry](https://ben-page.github.io/conduit/dispatch-retry/) or `Conduit.NET.Resilience`.

## Run the samples

Four sample applications live in [`samples/`](https://github.com/ben-page/conduit/tree/main/samples): a browser app on
the generated client over SignalR, two services (one routes commands and queries to the other over SignalR), and a REST
API with Swagger UI. They reference the projects under `src/`, so build the repository first. That build includes the
browser sample's front end, so it needs Node.js:

```shell
dotnet build
dotnet run --project samples/browser-client/Conduit.Sample.Notes    # https://localhost:5002
```

[samples/README.md](https://github.com/ben-page/conduit/blob/main/samples/README.md) has the rest: prerequisites,
ports, the run order for the two services, and calling the HTTP envelope endpoint with `curl`.

## Reading order

### Learn

1. [Why Conduit](https://ben-page.github.io/conduit/): the argument, with examples
2. [Getting Started](https://ben-page.github.io/conduit/getting-started/): install, and dispatch a first command
3. [Core Concepts](https://ben-page.github.io/conduit/core-concepts/): commands, queries, events, their handlers and the mediator
4. [Pipeline Behaviors](https://ben-page.github.io/conduit/pipeline-behaviors/): work that wraps every dispatch
5. [Error Handling](https://ben-page.github.io/conduit/error-handling/): refusals, error codes and Problem Details
6. [Modular Monolith](https://ben-page.github.io/conduit/modular-monolith/): many modules, one host, one mediator
7. [Dispatch Flows](https://ben-page.github.io/conduit/dispatch-flows/): each dispatch path, step by step

### Connect

1. [Transports](https://ben-page.github.io/conduit/transports/): endpoints, routing and event broadcast
2. [SignalR](https://ben-page.github.io/conduit/transports/signalr/)
3. [TypeScript Client](https://ben-page.github.io/conduit/transports/typescript-client/)
4. [HTTP](https://ben-page.github.io/conduit/transports/http/): envelope and REST endpoints
5. [Azure Service Bus](https://ben-page.github.io/conduit/transports/azure-service-bus/)
6. [RabbitMQ](https://ben-page.github.io/conduit/transports/rabbitmq/)
7. [Amazon SQS](https://ben-page.github.io/conduit/transports/amazon-sqs/)
8. [ActiveMQ](https://ben-page.github.io/conduit/transports/activemq/)
9. [Security](https://ben-page.github.io/conduit/security/): what a client may dispatch, and who receives events

### Make it durable

1. [Persistence](https://ben-page.github.io/conduit/persistence/): choosing a database engine, and the tables each feature creates
2. [Outbox](https://ben-page.github.io/conduit/outbox/)
3. [Inbox](https://ben-page.github.io/conduit/inbox/)
4. [Dead-Letter Queue](https://ben-page.github.io/conduit/dead-letter-queue/)
5. [Background Jobs](https://ben-page.github.io/conduit/jobs/)
6. [Audit Trail](https://ben-page.github.io/conduit/audit/)
7. [Dispatch Retry](https://ben-page.github.io/conduit/dispatch-retry/)
8. [Resilience](https://ben-page.github.io/conduit/pipeline-behaviors/#resilience-polly): Polly retry and circuit breaking

### Reference

1. [Configuration Reference](https://ben-page.github.io/conduit/configuration-reference/)
2. [Diagnostics](https://ben-page.github.io/conduit/diagnostics/): CONDUIT001 to CONDUIT008
3. [Glossary](https://ben-page.github.io/conduit/glossary/)
4. [FAQ](https://ben-page.github.io/conduit/faq/)

---

[Contributing](https://github.com/ben-page/conduit/blob/main/CONTRIBUTING.md) ·
[Security policy](https://github.com/ben-page/conduit/blob/main/SECURITY.md) ·
[Changelog](https://github.com/ben-page/conduit/blob/main/CHANGELOG.md) · Licensed under the
[Mozilla Public License 2.0](https://github.com/ben-page/conduit/blob/main/LICENSE)
