# Conduit.Transport.AmazonSqs

- `AmazonSqsTransport` — extends `BrokerTransportBase<AmazonSqsInboundMessage>`. Uses AWS SDK (`AmazonSNSClient` + `AmazonSQSClient`). Inbound dispatch, lifecycle, and outbound serialization handled by base class. Implements SNS/SQS-specific primitives: SNS publishing, SQS long-polling consumers, reply queue correlation, topology creation.
- `UseAmazonSqs(configure)` registers: singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (for event broadcast), keyed `IConduitTransport` (for endpoint routing), and calls `UseTransport<AmazonSqsTransport>()`. No connection string — uses Region or ServiceUri (for LocalStack).
- SNS topic for all messages, SQS queues per app, SNS subscriptions with filter policies route by type
- Commands/queries: shared subscription names (unicast). Events: per-app subscription names (multicast).
- Reply via direct SQS send (bypasses SNS). Message attributes: Type, MessageId, CausationId, ConversationId, ReplyQueueUrl, CorrelationId.
- Raw message delivery required on subscriptions (filter policies need it). Startup validates `RawMessageDelivery=true` and `VisibilityTimeoutSeconds >= ResponseTimeout`.
- SQS queue name limit is 80 characters — `TopologyNaming` hash truncation activates frequently.
- Topology modes: Production = standard, Developer/Test = tagged resources with background cleanup.
- Concurrency: options inherit `BrokerTransportOptions` (core). `DefaultConcurrency` and `ReceiverPoolOptions.Concurrency` map to the polling parallelism bound (the per-consumer `SemaphoreSlim` limit).
- Type discovery via `ConduitTypeRegistry` (source-generated, compile-time verified) — no runtime DI scanning
