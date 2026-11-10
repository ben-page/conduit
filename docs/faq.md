# FAQ

- [Which packages do I need?](#which-packages-do-i-need)
- [Can a client send any message my host handles?](#can-a-client-send-any-message-my-host-handles)
- [Why does my host warn CONDUIT001 about a contracts library's commands?](#why-does-my-host-warn-conduit001-about-a-contracts-librarys-commands)
- [How do I unit-test a handler?](#how-do-i-unit-test-a-handler)
- [Do events route like commands?](#do-events-route-like-commands)
- [Where is the generated code?](#where-is-the-generated-code)
- [How does Conduit compare to MediatR?](#how-does-conduit-compare-to-mediatr)
- [Why is there no Kafka transport?](#why-is-there-no-kafka-transport)
- [Why Task and not ValueTask?](#why-task-and-not-valuetask)
- [Was Conduit written with AI?](#was-conduit-written-with-ai)

## Which packages do I need?

Every package id starts `Conduit.NET`; the namespaces are `Conduit`, `Conduit.Messages` and so on.

| Project | Packages |
|---|---|
| The host: the project that calls `AddConduit()` | `Conduit.NET` and `Conduit.NET.SourceGen` |
| A library that only declares messages, such as contracts shared with another service | `Conduit.NET.Contracts` |
| A library of handlers, in a host made of several projects | `Conduit.NET`, without the generator ([Modular Monolith](modular-monolith.md)) |

Then one package per feature the host uses:

| Feature | Package |
|---|---|
| HTTP endpoints, or calling another service over HTTP | `Conduit.NET.Transport.Http` |
| A SignalR hub for browsers, or calling another service's hub | `Conduit.NET.Transport.SignalR` |
| A message broker | `Conduit.NET.Transport.RabbitMQ`, `.AzureServiceBus`, `.AmazonSqs` or `.ActiveMQ` |
| A typed TypeScript client | `Conduit.NET.TypeGen` |
| Outbox, inbox, dead-letter queue | `Conduit.NET.Durability`, with a database engine |
| Audit trail | `Conduit.NET.Audit`; its listener needs a database engine |
| Background jobs | `Conduit.NET.Jobs`; a database engine makes them cluster-safe |
| Retry and circuit breaking with Polly | `Conduit.NET.Resilience` |
| A database engine | `Conduit.NET.Persistence.Sqlite`, `.Postgres`, `.SqlServer` or `.MySql` |

## Can a client send any message my host handles?

Yes, until you add a type filter. Once the HTTP endpoint or the SignalR hub is mapped, a caller that can reach it may
send any command, query or event the host handles or routes. `AddTypeFilter` restricts every inbound wire edge (the
HTTP endpoint, the hub and every broker transport) to the full type names you list with `AllowType`. Any other type
is refused with 403 Forbidden before its payload is read, events included, and the refusal is answered to the caller.
The filter does not apply to a RESTful endpoint, which maps one type you chose, or to a dispatch in the process.
Who may reach the endpoint at all is ASP.NET Core's authorization. See [Security](security.md#typefilterinterceptor).

## Why does my host warn CONDUIT001 about a contracts library's commands?

The generator runs in the host, the project that references `Conduit.NET.SourceGen`, and discovers every command and
query the host can see, in referenced assemblies too. A contracts library shared with another service brings the
commands this host only sends: they have no handler here, so the host warns CONDUIT001 for each. Routing them does not
silence it. Declare them remote in the host project:

```csharp
[assembly: RemoteContracts("Inventory.Contracts")]
```

That covers the namespace and those beneath it, on whole segments, and changes no routing. The contracts library
itself references only `Conduit.NET.Contracts`, runs no generator, and never warns. See
[Diagnostics → CONDUIT001](diagnostics.md#conduit001--no-handler-found).

## How do I unit-test a handler?

A handler is a plain class: construct it and call it. No mediator, host or container is needed.

```csharp
var store = new InMemoryTodoStore();
var handler = new CreateTodoHandler(store);

var id = await handler.HandleAsync(new CreateTodoCommand("Buy milk"), CancellationToken.None);
```

To test a handler with its pipeline behaviors, its validation and the listeners of the events it publishes, dispatch
through `IMediator` in a real container. That needs the generated `AddConduit()`: start the host itself (ASP.NET
Core's `WebApplicationFactory<Program>`), or reference `Conduit.NET.SourceGen` from the test project, whose generator
then finds the public handlers of the projects the test references, and register Conduit there:

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<ITodoStore, InMemoryTodoStore>();
services.AddConduit();

await using var provider = services.BuildServiceProvider();
var mediator = provider.GetRequiredService<IMediator>();

var id = await mediator.ExecuteAsync(new CreateTodoCommand("Buy milk"));
```

A `[FireAndForget]` handler runs on a background service, which only a started host runs.

## Do events route like commands?

No. A route (`RouteNamespace<T>()`, `RouteType<T>()`) sends a command or a query to one endpoint. An event is
broadcast: `PublishAsync` runs this host's listeners, then hands the event to every server-mode and broker transport
the host registered, and each delivers it its own way, a broker to every service subscribed to its type, a SignalR
hub to its browsers ([Security → Who receives an event](security.md#who-receives-an-event)). A client-mode HTTP or
SignalR endpoint receives no events. `[LocalOnly]` keeps an event in the process. See
[Transports → Event broadcasting](transports/index.md#event-broadcasting).

## Where is the generated code?

In the compilation of the project that references `Conduit.NET.SourceGen`, not on disk. Your IDE lists it under the
project's analyzers, as `Conduit.SourceGen`. To write it to disk as well:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

The files then land in `obj/{Configuration}/{TargetFramework}/generated/Conduit.SourceGen/Conduit.SourceGen.ConduitGenerator/`:
`ConduitMediator.g.cs`, `ConduitDispatch.g.cs` and `ConduitRegistration.g.cs`, with `ConduitRestEndpoints.g.cs` when
the project references the HTTP transport and `ConduitResilienceDispatch.g.cs` when it references Resilience. All of
it is in the `Conduit.Generated` namespace.

## How does Conduit compare to MediatR?

The shape is the same: a message, one handler for a command or a query, any number of listeners for an event, and
pipeline behaviors around them. The differences:

- **The intent is in the type.** `ICommand` or `ICommand<TResult>`, `IQuery<TResult>` and `IEvent`, in place of
  `IRequest<T>` and `INotification`; dispatched with `ExecuteAsync`, `FetchAsync` and `PublishAsync`, and handled by
  `ICommandHandler`, `IQueryHandler` and `IEventListener`.
- **Handlers are found at compile time.** The source generator writes the registrations and a switch from message type
  to handler, so nothing scans assemblies or uses reflection to find a handler. A command or query with no handler is
  a build warning (CONDUIT001), and one with two handlers a build error (CONDUIT002).
- **You never return `Unit`.** A void command's handler returns `Task`. `Unit.Value` only appears as what a pipeline
  behavior gets back from `next()` for a void command or an event.
- **Conduit orders the pipeline,** not DI: by `AddPipeline` call, first registered outermost.
- **The same messages cross processes.** A route sends a command to another service, and the hub and the TypeScript
  client bring the messages to a browser.

## Why is there no Kafka transport?

A Kafka transport existed in the unreleased builds and was dropped before the first release. Kafka's partitions and
consumer groups fit neither the topology the other broker transports share (subscriptions that route each message type
to the queues of the services that handle it) nor request/response, where one waiting instance must get the reply.
The broker transports are RabbitMQ, Azure Service Bus, Amazon SQS and ActiveMQ.

## Why Task and not ValueTask?

Handlers, pipeline behaviors and `IMediator` return `Task`. `ValueTask` saves an allocation only when a call completes
without waiting, which a dispatch that reaches a database or a network rarely does, and it is harder to use correctly:
it may be awaited only once, and it does not fit the `Func<Task<TResult>>` chain that pipeline behaviors pass along.
The reasoning is kept in the [design notes](history/design-notes.md#task-not-valuetask).

## Was Conduit written with AI?

Conduit was built with substantial help from an AI assistant (Claude), under a design a person specified and reviewed
feature by feature. Every change is held to the same bar either way: warnings as errors, the analyzers on, and tests
for the behavior.

---

Previous: [Configuration Reference](configuration-reference.md) | [Home](../README.md)
