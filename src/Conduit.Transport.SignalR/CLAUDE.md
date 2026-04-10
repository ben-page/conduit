# Conduit.Transport.SignalR

- `ConduitHub` — inbound SignalR hub with `Dispatch(type, payload, context?)` and `Publish(type, payload, context?)` methods, injects `IWireMediator` via constructor. The `context` parameter is a `Dictionary<string, string>?` for `MessageContext` propagation — browser clients pass `null`, C# clients populate it.
- `SignalRTransport` — outbound dual-mode transport with lazy connect (`EnsureConnectedAsync`). `UseSignalR(hubUri)` registers as keyed singleton by endpoint name and calls `UseTransport<SignalRTransport>()`. Client mode sends `MessageContext.Current` as the context dictionary.
- `AddConduitSignalR()` registers SignalR services and server-mode transport as non-keyed `IConduitTransport` (for event broadcast to connected clients)
- **Dual-mode SignalRTransport**: Server mode (IHubContext, pushes events) and client mode (HubConnection, sends commands + receives events). Client mode uses lazy connect (auto-starts on first use via `EnsureConnectedAsync`).
