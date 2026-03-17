# Conduit

A source-generated mediator and messaging library for .NET 10. Define commands, queries, and events as plain C# records. The Roslyn source generator emits dispatch logic, DI registration, and a fully-typed TypeScript client at compile time with zero reflection.

## Why Conduit

Messaging should be easy to start and stay easy as your application grows. Define a record, implement a handler, and the source generator takes care of dispatch, DI registration, and TypeScript clients. Everything is emitted at compile time as plain readable code.

A single-service app with in-process dispatch takes one line of setup. Adding real-time SignalR takes two more. Exposing REST endpoints is a one-liner per route. Connecting services through Azure Service Bus or RabbitMQ is a configuration change. Your handlers and message types never move.

The same commands and queries work in-process, over RESTful HTTP with full OpenAPI support, or bidirectionally over SignalR with a generated TypeScript client that gives your browser full IDE autocomplete against your C# contracts. Use REST for external consumers, a real-time channel for your own UI, or both side by side.

## Scale without rewriting

Handlers implement `ICommandHandler<T>` or `IEventListener<T>` and receive messages directly. They never reference a transport. Where a message came from is determined by configuration, not code.

- **Start local.** In-process dispatch with zero infrastructure. Commands route to handlers, events broadcast to listeners, all in a single process.
- **Add SignalR.** Bidirectional real-time communication. Browser clients dispatch commands and receive events on the same connection. Services call each other over persistent connections with automatic reconnect.
- **Add REST endpoints.** Map commands and queries to conventional HTTP routes with `MapConduitGet`, `MapConduitPost`, and friends. Auth, OpenAPI, rate limiting all chain normally.
- **Add a service bus.** Azure Service Bus and RabbitMQ provide durable messaging with persistence, retry, and dead-lettering for production workloads.

At each step, the same message types and handler implementations work unchanged. Routing configuration determines where messages go.

## Guide

1. [Getting Started](docs/getting-started.md)
2. [Core Concepts](docs/core-concepts.md)
3. [Transports](docs/transports/index.md)
4. [SignalR](docs/transports/signalr.md)
5. [TypeScript Client](docs/transports/typescript-client.md)
6. [HTTP](docs/transports/http.md)
7. [Azure Service Bus](docs/transports/azure-service-bus.md)
8. [RabbitMQ](docs/transports/rabbitmq.md)
9. [Pipeline Behaviors](docs/pipeline-behaviors.md)
10. [Error Handling](docs/error-handling.md)
11. [Security](docs/security.md)
12. [Configuration Reference](docs/configuration-reference.md)
13. [FAQ](docs/faq.md)
