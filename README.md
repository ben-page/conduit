# Conduit

A source-generated mediator for .NET 10, with a typed TypeScript client and transports for HTTP, SignalR and four
message brokers. Commands, queries and events are plain C# records; the mediator, its dispatch code and its DI
registration are emitted at compile time, so dispatch needs no reflection and no assembly scanning.

## Why Conduit

Your front end and your back end are one application. The usual way to join them is an API designed for strangers: a
REST surface whose resource model fits no screen, DTOs and the mapping between them, and business rules that drift
into the browser because a REST update can only send a state diff. With CQRS, where every read model is bespoke, that
boundary decouples nothing.

Conduit lets the front end call the application directly. A screen is a query with a read model of its own. A button
is a command that names the user's intent, and the rules stay in its handler. The browser dispatches both through a
generated, typed `ConduitClient` to the same `IQueryHandler` and `ICommandHandler` your C# code calls and, over
SignalR, receives events on the same connection. When a consumer really is a stranger, map the same handler to a REST
route. When a handler moves to another service, route its messages there; the handler does not change.

The argument in full, with examples: [Why Conduit](docs/why-conduit.md).

## Packages

The ids below use the default prefix, `Conduit`. The prefix may change before the first release.

| Package | What it does |
|---|---|
| `Conduit` | The runtime: message and handler interfaces, routing, the dispatch pipeline, configuration |
| `Conduit.SourceGen` | The source generator: emits the mediator and `AddConduit()` into the host project |
| `Conduit.Contracts` | Message interfaces, attributes and exceptions with no package dependencies, for contract and domain libraries (`Conduit` brings it in) |
| `Conduit.TypeGen` | An MSBuild task that writes the typed TypeScript client, with its HTTP and SignalR transports |
| `Conduit.Transport.Http` | Envelope and REST endpoints that receive messages; a client that routes commands and queries to another service |
| `Conduit.Transport.SignalR` | A hub that serves browsers and services and pushes events to browsers; a client that routes to another service's hub |
| `Conduit.Transport.AzureServiceBus` | Azure Service Bus: a topic and its subscriptions |
| `Conduit.Transport.RabbitMQ` | RabbitMQ: exchanges and queues |
| `Conduit.Transport.AmazonSqs` | Amazon SQS queues, with SNS topics for fan-out |
| `Conduit.Transport.ActiveMQ` | ActiveMQ Artemis over AMQP 1.0 |
| `Conduit.Durability` | A transactional outbox, an idempotent inbox and a dead-letter queue |
| `Conduit.Jobs` | Cron and interval schedules and event triggers that run commands, once across the cluster when a database engine is registered |
| `Conduit.Audit` | An audit trail of every dispatch |
| `Conduit.Resilience` | Polly retry and circuit-breaker pipelines around each dispatch |
| `Conduit.Persistence` | The storage abstraction that Durability, Audit and Jobs share |
| `Conduit.Persistence.Sqlite`, `Conduit.Persistence.Postgres`, `Conduit.Persistence.SqlServer`, `Conduit.Persistence.MySql` | The database engines; reference one |

A broker transport dispatches each message once and does not retry it. A failed request is answered to its caller. An
event or fire-and-forget command whose handler fails goes to Conduit's [dead-letter queue](docs/dead-letter-queue.md)
when that is enabled, and otherwise back to the broker, whose own redelivery and dead-letter settings apply. Retrying
is something you opt into around the handler: [Dispatch Retry](docs/dispatch-retry.md) or `Conduit.Resilience`.

## Install

```shell
dotnet add package Conduit
dotnet add package Conduit.SourceGen
```

`Conduit.SourceGen` must be referenced explicitly, from the host project. The generator deliberately does not ride
along with the `Conduit` runtime package: a library that references `Conduit` for its messages and handlers runs no
generator, and the one host that composes them emits the single mediator for the process (see
[Modular Monolith](docs/modular-monolith.md)).

Add a transport package for each way messages enter or leave the host, and `Conduit.TypeGen` for a browser client:

```shell
dotnet add package Conduit.Transport.SignalR    # or .Http, .AzureServiceBus, .RabbitMQ, .AmazonSqs, .ActiveMQ
dotnet add package Conduit.TypeGen
```

`Conduit.TypeGen` runs after each build of a project that sets `<ConduitTypeGenEnabled>true</ConduitTypeGenEnabled>`,
and writes `conduit.ts` and `conduit.generated.ts` (to `ClientApp/generated` by default). Their SignalR transport
imports the `@microsoft/signalr` npm package. Build with the `dotnet` CLI: Visual Studio's own `msbuild.exe` cannot
load the task. See [TypeScript Client](docs/transports/typescript-client.md).

## Run the samples

Four sample applications live in [`samples/`](samples): a browser app on the generated client over SignalR, two
services (one routes commands and queries to the other over SignalR), and a REST API with Swagger UI. They reference
the projects under `src/`, so build the repository first. That build includes the browser sample's front end, so it
needs Node.js:

```shell
dotnet build
dotnet run --project samples/browser-client/Conduit.Sample.Notes    # https://localhost:5002
```

[samples/README.md](samples/README.md) has the rest: prerequisites, ports, the run order for the two services, and
calling the HTTP envelope endpoint with `curl`.

## Reading order

### Learn

1. [Why Conduit](docs/why-conduit.md): the argument, with examples
2. [Getting Started](docs/getting-started.md): install, and dispatch a first command
3. [Core Concepts](docs/core-concepts.md): commands, queries, events, their handlers and the mediator
4. [Pipeline Behaviors](docs/pipeline-behaviors.md): work that wraps every dispatch
5. [Error Handling](docs/error-handling.md): refusals, error codes and Problem Details
6. [Modular Monolith](docs/modular-monolith.md): many modules, one host, one mediator
7. [Dispatch Flows](docs/dispatch-flows.md): each dispatch path, step by step

### Connect

1. [Transports](docs/transports/index.md): endpoints, routing and event broadcast
2. [SignalR](docs/transports/signalr.md)
3. [TypeScript Client](docs/transports/typescript-client.md)
4. [HTTP](docs/transports/http.md): envelope and REST endpoints
5. [Azure Service Bus](docs/transports/azure-service-bus.md)
6. [RabbitMQ](docs/transports/rabbitmq.md)
7. [Amazon SQS](docs/transports/amazon-sqs.md)
8. [ActiveMQ](docs/transports/activemq.md)
9. [Security](docs/security.md): what a client may dispatch, and who receives events

### Make it durable

1. [Persistence](docs/persistence.md): choosing a database engine, and the tables each feature creates
2. [Outbox](docs/outbox.md)
3. [Inbox](docs/inbox.md)
4. [Dead-Letter Queue](docs/dead-letter-queue.md)
5. [Background Jobs](docs/jobs.md)
6. [Audit Trail](docs/audit.md)
7. [Dispatch Retry](docs/dispatch-retry.md)
8. [Resilience](docs/pipeline-behaviors.md#resilience-polly): Polly retry and circuit breaking

### Reference

1. [Configuration Reference](docs/configuration-reference.md)
2. [Diagnostics](docs/diagnostics.md): CONDUIT001 to CONDUIT008
3. [Glossary](docs/glossary.md)
4. [FAQ](docs/faq.md)

---

[Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md) · [Changelog](CHANGELOG.md) · Licensed under the
[Mozilla Public License 2.0](LICENSE)
