# Conduit.Transport.ActiveMQ

- `ActiveMQTransport` — extends `BrokerTransportBase<ActiveMQInboundMessage>`. Uses `AmqpNetLite` (AMQP 1.0). Inbound dispatch, lifecycle, and outbound serialization handled by base class. Implements Artemis-specific primitives: AMQP 1.0 connection/session management, two-address publishing, exclusive reply queue correlation, receiver link management.
- `UseActiveMQ(connectionString, configure?)` registers: singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (for event broadcast), keyed `IConduitTransport` (for endpoint routing), and calls `UseTransport<ActiveMQTransport>()`
- Two addresses: anycast for commands/queries, multicast for events
- Command/query queues: shared names on anycast address (unicast). Event queues: per-app names on multicast address (multicast).
- Reply queue: per-instance exclusive temporary queue (destroyed on shutdown)
- AMQP 1.0 wire mapping: `Subject`=FullTypeName, native `MessageId`, native `CorrelationId`=ConversationId; `ApplicationProperties` carry the historical `CausationId` (plus `ReplyTo`/`ReplyCorrelationId` for requests) alongside the canonical `conduit-message-id`/`conduit-causation-id`/`conduit-conversation-id`/`conduit-source-app` keys — every header the base class assembles is additionally written under its canonical `ConduitHeaders` name (`BuildOutboundApplicationProperties`). `conduit-source-app` carries the publishing application's `ApplicationName` (omitted when unset) and is read by the [self-published event filter](../../docs/transports/index.md#self-published-events) on the inbound side.
- Topology via Artemis management address (`activemq.management`). Topology modes: Production = durable, Developer = auto-delete-delay 4h, Test = auto-delete-delay 5min (native Artemis support).
- Concurrency via AMQP link credit (prefetch count). Options inherit `BrokerTransportOptions` (core): `DefaultConcurrency` and `ReceiverPoolOptions.Concurrency` both map to the receiver link prefetch count.
- Type discovery via `ConduitTypeRegistry` (source-generated, compile-time verified) — no runtime DI scanning
