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

Next: [Getting Started](getting-started.md)
