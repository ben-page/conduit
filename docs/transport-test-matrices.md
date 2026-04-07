# Transport Test Matrices

Definitive test matrices for broker transport implementations. Every new transport MUST pass all universal tests. Transport-specific tests are added as needed for broker-specific features.

Each transport has its own test project: `tests/Conduit.Transport.{Name}.Tests/` with `Integration/` for behavior tests and `Unit/` for topology manager tests.

---

## Matrix 1: Transport Behavior Tests

Multi-host integration tests validating message routing correctness. Each test spins up 2-3 in-process `TestHost` instances connected to the same broker. Tests live in `Integration/` within each transport's test project.

### Test Infrastructure

Each transport test project contains:
- `Support/Contracts/` — 16 contract types (commands, queries, events, results) discovered by the source generator
- `Support/Handlers/` — 13 handlers/listeners recording invocations via `SpyLog`
- `Integration/{Transport}Fixture.cs` — extends `TransportTestFixture` from `Conduit.TestUtils`, manages broker container via Testcontainers

Shared infrastructure in `Conduit.TestUtils`:
- `SpyLog` — thread-safe invocation recorder with `WaitForCountAsync`
- `MessageContextSnapshot` — captures `MessageId`, `CausationId`, `ConversationId`
- `TransportTestFixture` — abstract base with `CreateHostAsync`, `CreateSenderHostAsync`, `DisposeHostsAsync`, `UniqueAppName`
- `TestHost` — lightweight `IHost` wrapper that starts hosted services

### IMediator API

- `mediator.ExecuteAsync(ICommand<TResult> command)` — commands with results
- `mediator.ExecuteAsync(ICommand command)` — void commands
- `mediator.FetchAsync(IQuery<TResult> query)` — queries
- `mediator.PublishAsync(IEvent @event)` — events

### Category 1: Command Routing

Test class: `{Transport}CommandRoutingShould`

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 1.1 | `RouteCommandToCorrectEndpoint` | A sends, B handles, C present (bystander) | B spy == 1, C spy == 0 |
| 1.2 | `ReturnCommandResponseToCaller` | A sends, B handles | A receives `DispatchResult<T>` with expected data |
| 1.3 | `RouteVoidCommandAndComplete` | A sends, B handles | B spy == 1, A succeeds |
| 1.4 | `PropagateConduitExceptionAsFailResult` | A sends, B throws `NotFoundException` | A gets `InvalidOperationException` with error code |
| 1.5 | `ReturnInternalErrorForUnexpectedException` | A sends, B throws `InvalidOperationException` | A gets `InternalError`, no details leaked |
| 1.6 | `TimeoutWhenNoHandlerAvailable` | A sends, no handler host | Throws after `ResponseTimeout` (use short 5s timeout) |
| 1.7 | `DeliverFireAndForgetCommandToHandler` | A sends `[FireAndForget]` command, B handles | B spy == 1 with correct data, no reply expected |

### Category 2: Query Routing

Test class: `{Transport}QueryRoutingShould`

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 2.1 | `RouteQueryToCorrectEndpoint` | A sends, B handles, C present (bystander) | B spy == 1, C spy == 0 |
| 2.2 | `ReturnQueryResponseWithCorrectData` | A sends, B handles | Result value matches expected |
| 2.3 | `PropagateConduitExceptionFromQuery` | A sends, B throws `NotFoundException` | Error propagated |
| 2.4 | `TimeoutQueryWhenNoHandler` | A sends, no handler host | Throws after `ResponseTimeout` |

### Category 3: Event Routing

Test class: `{Transport}EventRoutingShould`

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 3.1 | `DeliverEventToAllSubscribingApps` | A publishes, B + C listen (different app names) | B spy == 1, C spy == 1 |
| 3.2 | `NotBroadcastLocalOnlyEvent` | A publishes `[LocalOnly]` event | A local spy == 1, B spy == 0 |
| 3.3 | `DeliverEventToListenerDespiteOtherListenerFailure` | B has throwing listener for `ThrowingTestEvent` | Spy records before throwing (count >= 1) |
| 3.4 | `PropagateWireEventListenerFailure` | A publishes, B listener throws | Transport retry/dead-letter semantics exercised |
| 3.5 | `DeliverFireAndForgetEventToSubscribingApp` | A publishes `[FireAndForget]` event, B listens | B spy == 1 (F&F only affects local listeners, transport broadcast unchanged) |

### Category 4: Competing Instance Isolation

Test class: `{Transport}CompetingInstanceShould`

| # | Test Method | DataRow | Hosts | Assert |
|---|---|---|---|---|
| 4.1 | `DeliverCommandToExactlyOneInstance` | `[DataRow(1)] [DataRow(20)]` | A sends N, B1+B2+B3 (same app name) | Total invocations across B instances == N |
| 4.2 | `DeliverQueryToExactlyOneInstance` | `[DataRow(1)] [DataRow(20)]` | A sends N, B1+B2+B3 (same app name) | Total invocations across B instances == N |
| 4.3 | `DeliverEventToExactlyOneInstancePerApp` | `[DataRow(1)] [DataRow(20)]` | A publishes N, B1+B2 + C1+C2 | Total B == N, Total C == N |

### Category 5: MessageContext Propagation

Test class: `{Transport}MessageContextShould`

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 5.1 | `PropagateMessageContextForCommand` | A sends `ContextCaptureCommand`, B captures | MessageId, CausationId, ConversationId all non-null |
| 5.2 | `PropagateMessageContextForQuery` | A sends `ContextCaptureQuery`, B captures | Same |
| 5.3 | `PropagateMessageContextForEvent` | A publishes `ContextCaptureEvent`, B captures | Same |
| 5.4 | `PreserveConversationIdInNestedDispatch` | A sends `NestingCommand`, B handles + dispatches locally | ConversationId consistent, CausationId chains |

### Category 6: Receiver Pools

Test class: `{Transport}ReceiverPoolShould`

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 6.1 | `RouteMessageToCorrectPool` | A sends `SlowCommand`, B configured with pool | Handler invoked via pool consumer |
| 6.2 | `RouteUnassignedTypesToDefaultPool` | A sends `TestCommand` (not in pool), B has pool configured | Default consumer handles it |
| 6.3 | `IsolatePoolConcurrency` | A sends slow + fast concurrently, B has 2 pools | Fast pool completes while slow is processing |

### Category 7: Topology Modes

Test class: `{Transport}TopologyModeShould`

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 7.1 | `CreateDurableResourcesInProductionMode` | A + B with `TopologyMode.Production` | Host starts successfully, resources created |
| 7.2 | `CreateEphemeralResourcesInDeveloperMode` | A + B with `TopologyMode.Developer` | Host starts, resources have auto-delete/TTL flags |
| 7.3 | `IsolateTestModeRuns` | Two independent test runs with `TopologyMode.Test` | Different prefixes, no cross-contamination |
| 7.4 | `CleanupStaleResources` *(Kafka, SQS only)* | Single host, developer mode | Background cleanup removes resources after TTL |

### Totals

30 test scenarios per transport (29 for transports without 7.4).

---

## Matrix 2: Topology Manager Unit Tests

Unit tests validating that each transport's topology manager creates the correct broker resources with correct names, routing semantics, and durability settings. Tests live in `Unit/TopologyManagerShould.cs` within each transport's test project.

These tests verify naming logic and configuration without requiring a live broker. They test the topology manager's resource plan — the names it computes and the flags it sets.

### Category TM-1: Resource Naming

| # | Test Method | Validates |
|---|---|---|
| TM-1.1 | `UseCorrectMaxLengthForBroker` | Passes correct maxLength to `TopologyNaming` (RMQ=255, ASB=260/50, Kafka=249, SQS=80/256, ActiveMQ=256) |
| TM-1.2 | `UseSharedResourceNameForMainTopic` | Main topic/exchange/address uses `TopologyNaming.CreateSharedResourceName(options, maxLen)` |
| TM-1.3 | `UseAppResourceNameForAppQueue` | Per-app queue uses `TopologyNaming.CreateAppResourceName(options, maxLen)` |
| TM-1.4 | `UseAppResourceNameForReplyQueue` | Reply resource uses `TopologyNaming.CreateAppResourceName(options, "replies", maxLen)` |
| TM-1.5 | `UseSharedResourceNameForCommandQueues` | Command/query queue names use `TopologyNaming.CreateSharedResourceName(options, type, maxLen)` — shared across apps (unicast) |
| TM-1.6 | `UseAppResourceNameForEventQueues` | Event queue names use `TopologyNaming.CreateAppResourceName(options, type, maxLen)` — per-app (multicast) |
| TM-1.7 | `TruncateLongNamesWithHash` | Names exceeding maxLength are truncated with XxHash32 suffix |
| TM-1.8 | `IncludeTopologyPrefixInNames` | Developer/Test prefix appears in all resource names |

### Category TM-2: Unicast vs Multicast Routing

| # | Test Method | Validates |
|---|---|---|
| TM-2.1 | `RouteCommandsToSharedResources` | Command subscriptions/bindings/queues use shared names — competing consumers deliver to exactly one app |
| TM-2.2 | `RouteEventsToAppScopedResources` | Event subscriptions/bindings/queues use app-scoped names — every subscribing app gets a copy |

### Category TM-3: Pool Routing

| # | Test Method | Validates |
|---|---|---|
| TM-3.1 | `RoutePoolTypesToPoolQueue` | Message types assigned to a pool are routed to the pool's queue |
| TM-3.2 | `RouteNonPoolTypesToDefaultQueue` | Message types not in any pool fall back to the default app queue |
| TM-3.3 | `ExcludePoolTypesFromDefaultQueue` | Pool-assigned types are NOT bound to the default queue (no double delivery) |

### Category TM-4: Topology Modes

| # | Test Method | Validates |
|---|---|---|
| TM-4.1 | `CreateDurableResourcesInProductionMode` | Production mode: durable=true, no auto-delete, no TTL |
| TM-4.2 | `CreateEphemeralResourcesInDeveloperMode` | Developer mode: auto-delete with 4-hour TTL |
| TM-4.3 | `CreateEphemeralResourcesInTestMode` | Test mode: auto-delete with 5-minute TTL |

### Category TM-S: Transport-Specific

These tests validate broker-specific topology features that don't apply to all transports.

#### RabbitMQ

| # | Test Method | Validates |
|---|---|---|
| TM-S.1 | `CreateTopicExchangeType` | Exchange type is `ExchangeType.Topic` |

#### Azure Service Bus

| # | Test Method | Validates |
|---|---|---|
| TM-S.2 | `CreateSqlFilterOnSubscriptions` | Subscriptions have SQL filter: `Subject = '{FullTypeName}'` |
| TM-S.3 | `CreateSessionEnabledReplyQueue` | Reply queue has `RequiresSession = true` |

#### Kafka

| # | Test Method | Validates |
|---|---|---|
| TM-S.4 | `SetRetentionForEphemeralTopics` | `retention.ms` config matches topology mode (Developer=4h, Test=5min) |
| TM-S.5 | `CreateDeadLetterTopic` | Dead-letter topic created with correct name |
| TM-S.6 | `CreateMainTopicWithCorrectPartitions` | Shared topic has configured partition count |

#### Amazon SQS (+SNS)

| # | Test Method | Validates |
|---|---|---|
| TM-S.7 | `CreateSnsFilterPolicy` | SNS subscription filter policy: `{"Type": ["FullTypeName"]}` |
| TM-S.8 | `EnableRawMessageDelivery` | Raw message delivery enabled on all SNS subscriptions |
| TM-S.9 | `SetSqsQueuePolicy` | SQS queue policy allows SNS topic to send messages |

#### ActiveMQ Artemis

| # | Test Method | Validates |
|---|---|---|
| TM-S.10 | `CreateSeparateAnycastAndMulticastAddresses` | Two addresses: anycast for commands/queries, multicast for events |
| TM-S.11 | `UseAmqpFilterOnQueues` | Queue filter expression: `AMQPSubject = '{FullTypeName}'` |
| TM-S.12 | `SetAutoDeleteDelayForEphemeralQueues` | Native `auto-delete-delay` in milliseconds per topology mode |

### Totals

16 universal tests + 12 transport-specific tests = 92 total test cases across 5 transports.

---

## Adding a New Transport

When adding a new broker transport, create a test project and implement:

1. **All 28 transport behavior tests** (Matrix 1) — copy the test class structure from an existing transport, adjust the fixture
2. **All 16 universal topology manager tests** (Matrix 2, TM-1.x through TM-4.x) — verify resource naming, routing semantics, pool routing, and topology modes
3. **Transport-specific topology tests** (Matrix 2, TM-S.x) — add tests for broker-specific features

### Test Project Structure

```
tests/Conduit.Transport.{Name}.Tests/
    Conduit.Transport.{Name}.Tests.csproj
    Support/
        Contracts/          # 16 contract types (source gen requires per-assembly)
        Handlers/           # 13 handler/listener types
    Integration/
        {Name}Fixture.cs    # Extends TransportTestFixture, manages container
        {Name}CommandRoutingShould.cs
        {Name}QueryRoutingShould.cs
        {Name}EventRoutingShould.cs
        {Name}CompetingInstanceShould.cs
        {Name}MessageContextShould.cs
        {Name}ReceiverPoolShould.cs
        {Name}TopologyModeShould.cs
    Unit/
        TopologyManagerShould.cs
```

### Source Gen Constraint

Contracts and handlers MUST live in the test project assembly — the source generator discovers types per compilation unit. They cannot be shared via a library reference. Copy from an existing transport's `Support/` directory and adjust the namespace.
