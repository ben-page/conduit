# Conduit.Transport.Kafka

- `KafkaTransport` — extends `BrokerTransportBase<KafkaInboundMessage>`. Uses `Confluent.Kafka`. Inbound dispatch, lifecycle, and outbound serialization handled by base class. Implements Kafka-specific primitives: producer/consumer management, consumer group routing, dead-letter topic publishing.
- `UseKafka(bootstrapServers, configure?)` registers: singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (for event broadcast), keyed `IConduitTransport` (for endpoint routing), and calls `UseTransport<KafkaTransport>()`
- Two consumer groups per app: shared for commands/queries (unicast), per-app for events (multicast). Both subscribe to the same topic; client-side filtering by message type.
- Reply topic per app with per-instance consumer group and manual partition assignment
- Wire format: `conduit-type`, `conduit-message-id`, `conduit-causation-id`, `conduit-conversation-id`, `conduit-reply-topic`, `conduit-correlation-id` headers (UTF-8 byte arrays). Message key = FullTypeName.
- `AbandonAsync` commits offset and publishes to dead-letter topic immediately (no in-process retry — Kafka offsets are sequential, not committing blocks all subsequent messages)
- Topology modes: Production = default retention, Developer = 4h retention, Test = 5min retention. Background cleanup task for ephemeral topics.
- Type discovery via `ConduitTypeRegistry` (source-generated, compile-time verified) — no runtime DI scanning
