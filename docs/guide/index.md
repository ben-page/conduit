# Conduit

A source-generated mediator and messaging library for .NET 10. Define commands, queries, and events as plain C# records, and the Roslyn source generator emits the dispatch logic, DI registration, and optionally a fully-typed TypeScript client — all at compile time with zero reflection.

## Why Conduit

Conduit is built around a simple idea: messaging should be easy to start and stay easy as your application grows. Define a C# record, implement a handler, and the source generator takes care of the rest — dispatch, DI registration, and TypeScript clients, all emitted at compile time as plain readable code.

A single-service app with in-process dispatch takes one line of setup. Adding real-time SignalR communication takes two more. Exposing REST endpoints is a one-liner per route. Connecting services through Azure Service Bus or RabbitMQ is a configuration change — your handlers and message types don't move. The API surface stays small and consistent whether you're building a weekend project or a distributed system.

The same commands and queries can be dispatched locally in-process, mapped directly to RESTful HTTP endpoints with full OpenAPI and auth support via ASP.NET Core minimal APIs, or streamed bidirectionally to the browser over SignalR with a generated TypeScript client that provides full IDE autocomplete against your C# contracts. Use conventional REST routes for external consumers, a real-time channel for your own UI, or both side by side — the browser isn't a separate app talking to your API, it's a participant in the same messaging topology.

## Scale without rewriting

Your handlers implement `ICommandHandler<T>` or `IEventListener<T>` and receive the message directly — they never reference a transport. Where the message came from (in-process, SignalR, HTTP, a service bus) is determined by configuration, not code.

- **Start local.** In-process dispatch with zero infrastructure. Commands route to handlers, events broadcast to listeners, all within a single process. This is enough for many applications and costs nothing to set up.
- **Add SignalR.** Two lines of setup give you bidirectional real-time communication. Browser clients dispatch commands through a hub and receive events on the same connection. Services call each other over persistent connections with automatic reconnect.
- **Add REST endpoints.** Map commands and queries directly to conventional HTTP routes with `MapConduitGet`, `MapConduitPost`, and friends. Standard ASP.NET Core chaining — auth, OpenAPI, rate limiting — works on every route.
- **Add a service bus.** Azure Service Bus and RabbitMQ transports provide durable, decoupled messaging for production workloads that need persistence, retry, and dead-lettering.

At each step, the same message types and handler implementations work unchanged. Routing configuration determines where messages go.

## Guide

1. [Getting Started](getting-started.md)
2. [Core Concepts](core-concepts.md)
3. [TypeScript Client](typescript-client.md)
4. [Event Transport](event-transport.md)
5. [SignalR Transport](signalr-transport.md)
6. [HTTP Transport](http-transport.md)
7. [Cross-Service Routing](cross-service-routing.md)
8. [Pipeline Behaviors](pipeline-behaviors.md)
9. [Error Handling](error-handling.md)
10. [Security](security.md)
11. [Configuration Reference](configuration-reference.md)
12. [FAQ](faq.md)
