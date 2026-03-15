# Conduit

Source-generated distributed messaging for .NET. Define commands, queries, and events as plain C# records. The source generator wires dispatch, DI registration, and TypeScript clients at compile time with zero reflection.

## Scale as you grow

- **Start local.** In-process dispatch with no infrastructure. Commands and queries route to handlers, events broadcast to listeners, all within a single process.
- **Add SignalR.** Real-time bidirectional communication with browsers and between services. Push events to connected clients, dispatch commands across service boundaries.
- **Add a service bus.** Durable, decoupled messaging across services for production workloads that need reliability and persistence.

At each step, the same message types and handlers work unchanged. Routing configuration determines where messages go; your application code stays the same.

## Typed TypeScript clients

Mark C# types with `[ClientVisible]` and Conduit generates a fully typed TypeScript client with IDE autocomplete. Request and response types flow from your C# contracts to the browser with zero hand-written API contracts.

## What Conduit provides

Conduit provides messaging patterns for distributed .NET services — command dispatch, event transport, typed clients — without the ceremony of a full service bus framework. Start simple, add infrastructure as you grow.

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
12. [AI-Assisted Development](ai-development.md)
