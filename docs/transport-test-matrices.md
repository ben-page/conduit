# Transport Test Matrices

Definitive test matrices for broker transport implementations. Every new transport MUST pass all universal tests. Transport-specific tests are added as needed for broker-specific features.

Each transport has its own test project: `tests/Conduit.Transport.{Name}.Tests/` with `Integration/` for behavior tests and `Unit/` for topology manager tests.

---

## Matrix 1: Transport Behavior Tests

Multi-host integration tests validating message routing correctness. Each test spins up 2-3 in-process `TestHost` instances connected to the same broker. Tests live in `Integration/` within each transport's test project.

### Test Infrastructure

Each transport test project contains:
- `Support/Contracts/` — 19 contract types (commands, queries, events, results, F&F types, the shutdown probe) discovered by the source generator (RabbitMQ: 20, with the `ConcurrencyProbeCommand` of its [transport-specific](#category-11-transport-specific-behavior) concurrency test)
- `Support/Handlers/` — 17 handlers/listeners recording invocations via `SpyLog` (RabbitMQ: 18, plus the `ConcurrencyProbeMark` its probe handler records)
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
| 1.6 | `TimeoutWhenNoHandlerAvailable` | A sends, no handler host | After `ResponseTimeout` (use short 5s timeout), throws exactly `TimeoutException("No reply to '{type}' from endpoint 'broker' within 00:00:05.")` — never an `OperationCanceledException` |
| 1.7 | `DeliverFireAndForgetCommandToHandler` | A sends `[FireAndForget]` command, B handles | B spy == 1 with correct data, no reply expected |

### Category 2: Query Routing

Test class: `{Transport}QueryRoutingShould`

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 2.1 | `RouteQueryToCorrectEndpoint` | A sends, B handles, C present (bystander) | B spy == 1, C spy == 0 |
| 2.2 | `ReturnQueryResponseWithCorrectData` | A sends, B handles | Result value matches expected |
| 2.3 | `PropagateConduitExceptionFromQuery` | A sends, B throws `NotFoundException` | Error propagated |
| 2.4 | `TimeoutQueryWhenNoHandler` | A sends, no handler host | Same as 1.6, for a query |

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
| 5.1 | `PropagateMessageContextForCommand` | A sends `ContextCaptureCommand`, B captures | MessageId, CausationId, ConversationId all non-null; head self-causates (CausationId == MessageId) |
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
| 7.4 | *(SQS only — separate test class, not a method in `{Transport}TopologyModeShould`)* | Single host, test mode | On startup of each test project, the project reaps every broker resource under its own stable `test-{hash8(assembly)}-` prefix. **SQS:** `AmazonSqsTopologyReaperShould.ReapQueuesAndTopicsMatchingThePrefixAndLeaveOthers` + once-per-project reap in `AssemblySetup`. **RabbitMQ/ASB/ActiveMQ:** N/A — native auto-delete/TTL covers cleanup (on ActiveMQ the queues; its two addresses per prefix stay, empty). |

**7.4 scope.** Cleanup is per test project, once at assembly initialize, scoped to resources the project created (a stable `test-{hash8(assembly)}-` prefix via `Conduit.TestUtils.TestTopologyPrefix`). A project only deletes its own resources, so concurrent projects/hosts never interfere. The transport libraries expose `ReapByPrefixAsync` so applications can run their own prefix-scoped cleanup too.

### Category 8: Dead-Letter Capture

Test class: `{Transport}DeadLetterShould`

| # | Test Method | Assert |
|---|---|---|
| 8.1 | `CaptureFailedEventToDeadLetterStore` | A failed inbound event is written to the dead-letter store |
| 8.2 | `NotCaptureABusinessRefusal` | A routed command refused with `NotFoundException` is not captured; a following command whose handler throws unexpectedly is, as the only entry, with the `Reference` its caller's `RemoteDispatchException` carried (the second capture is the barrier for the first's absence) |
| 8.3 | `AcknowledgeFailedMessageAfterCapture` | After capture, the broker message is acknowledged (not redelivered) |
| 8.4 | `ReplayDeadLetteredMessageBackToBroker` | A dead-lettered message can be replayed back through the transport |
| 8.5 | `CaptureAMalformedBody` | A body that is not JSON, sent natively straight to the app queue, is captured before dispatch: its type, `JsonException`, the body byte for byte; no listener ran |
| 8.6 | `ReplayWithTheOriginalIds` | An event published under a two-level context (three distinct ids) is captured, replayed from inside the test's own context, and recaptured with the original `MessageId`, `CausationId` and `ConversationId` |

### Category 9: Self-Published Event Delivery & Wire Correlation

Test class: `{Transport}SelfPublishedEventShould`

Universal — required of every transport, real broker only (the in-memory `FakeBrokerTransport` double in `Conduit.Tests` already covers this at the unit level; these two prove the same behavior actually holds on the wire).

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 9.1 | Self-published event runs local listeners exactly once | Single host both publishes and locally listens to `TestEvent` | Publish an event the app also listens to; assert one listener execution, not two — wait deliberately for the broker's own-copy round trip before asserting, since a bare `await` on publish passes vacuously either way |
| 9.2 | Correlation and trace context survive the wire | A publishes `ContextCaptureEvent` under a known `MessageContext` and `Activity`, B captures | Publish under a known MessageContext and Activity; assert MessageId, CausationId, ConversationId and traceparent arrive intact |

### Category 10: Shutdown with In-Flight Work

Test class: `{Transport}ShutdownShould`

Universal. Both send `ShutdownProbeCommand(Input, DelayMs)`, whose handler records itself on entry and then runs `DelayMs` ignoring cancellation. The container-free `BrokerTransportShutdownShould` in `Conduit.Tests` pins the same stop sequence on `FakeBrokerTransport` (plus a message delivered after stop began is requeued through `RequeueAsync`, neither dispatched nor abandoned: `RequeueAMessageDeliveredAfterStopBegins`); these prove it holds against the real broker.

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 10.1 | `CompleteAnInFlightHandlerBeforeClosing` | A sends (2 s handler), B1 + B2 (same app name) compete; the one that takes it is disposed 0.5 s into the handler | Disposal took more than 1 s (stop waited), A gets the handler's reply, and after a redelivery window (immediate for RabbitMQ/ActiveMQ, the visibility timeout — set to 5 s — for SQS) the standby has received nothing: the message was acknowledged once |
| 10.2 | `StopWhenTheHostTokenExpires` | A sends (10 s handler), B handles; `TestHost.StopAsync` with a 1 s token | Stop took at least ~1 s and well under the handler's 10 s; A's request then fails with `TimeoutException` |

Azure Service Bus's 10.1 is `SettleTheInFlightMessageOnStop`: the same hosts and assertions, plus a peek of the app queue right after the disposal, which must find nothing. Service Bus redelivers a message whose settle failed only once its lock expires (a minute by default), past any practical redelivery window, but the message is still in the queue to be peeked. Stopping the processor cancels `ProcessMessageEventArgs.CancellationToken`, so the transport replies and settles with `CancellationToken.None`; with the processor's token, this test fails on the peek.

### Category 11: Transport-specific behavior

Integration tests of one broker's own mechanics, beyond the shared matrix.

#### RabbitMQ

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 11.1 | `RabbitMqCommandRoutingShould.KeepReplyingAfterConnectionRecovery` | A sends (15 s `ResponseTimeout`), B handles; one round trip, then the broker container is restarted (`RabbitMqTestBase.RestartBrokerAsync`: Testcontainers `StopAsync`/`StartAsync`; Inconclusive for a configured or reused broker, or a re-mapped port) | Once both connections raise `RecoverySucceededAsync` (no fixed sleep; the transport's own handler, which re-declares the reply queue, runs first), a second request gets its reply: the client-named reply queue came back under the name its requests carry in `ReplyTo` |
| 11.2 | `RabbitMqConcurrencyShould.RunHandlersInParallelUpToConcurrency` | B with `DefaultConcurrency = 4`; A sends 8 `ConcurrencyProbeCommand`s (1 s handler) at once | All 8 answered, and replaying the handler's `ConcurrencyProbeMark`s gives exactly 4 running at once: in parallel, never beyond the concurrency |
| 11.3 | `RabbitMqDeadLetterShould.RouteAFailedEventToTheDeadLetterExchangeWithoutAConduitDlq` | A publishes `ThrowingTestEvent`, B listens with no dead-letter store | After 2 s the listener has run once (rejected, not requeued), and `{prefix}-conduit-dead-letter` holds the event once (`basic.get` twice: the event, then nothing), with `x-first-death-reason: rejected` and `x-first-death-queue` = B's app queue |
| 11.4 | `RabbitMqReplyQueueShould.FallBackToAFreshNameWhenTheNameIsLocked` | B handles; a test connection declares an exclusive queue under the reply queue name A would take (`InitialReplyQueueInstanceId` pins it); then A starts and sends | A starts despite `RESOURCE_LOCKED`, its request gets its reply (so the reply went to A's fresh queue), and the locked queue holds no message |

#### Azure Service Bus

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 11.5 | `AsbTopologyShould.UpdateASubscriptionWhenAPoolAssignmentChanges` | B's first release starts and stops with `TestEvent` in a pool `retired` (`TestCommand` on its app queue); its second release, same app name and prefix, drops `retired` and moves `TestCommand` into a new pool `moved`; A sends `TestCommand` | The second release starts; `TestEvent`'s subscription forwards to the app queue again (it pointed at a queue the release no longer has, so only its being app-scoped allows the redirect) and `TestCommand`'s to `moved`'s queue (it pointed at the app queue, one of the release's own); A gets the handler's reply. Removing either condition from the redirect check makes it fail |
| 11.6 | `AsbTopologyShould.RefuseASubscriptionThatForwardsToAnotherApplication` | B handles every command; C, another app name on the same prefix, also handling them, starts | C's start throws `InvalidOperationException` naming a collision between applications: a shared subscription forwarding to another application's queue is never taken over |
| 11.7 | `AsbShutdownShould.FailARequestStillAwaitingItsReplyOnStop` | A (30 s `ResponseTimeout`) sends a `ShutdownProbeCommand` (5 s handler), B handles; once B's handler is running, A is stopped | A's request throws exactly `InvalidOperationException("The transport for endpoint 'broker' stopped before the reply arrived.")` within 3 s of the stop: the session receive is raced against the waiter `FailPendingReplies` fails |

#### ActiveMQ

Several of these read what the broker holds through the Artemis management address (`Support/ArtemisManagement`: a queue's `messagesAdded`, `countMessages` with a filter, the configuration `updateQueue` answers with).

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 11.8 | `ActiveMQEventRoutingShould.NotDeliverAnotherAppsEventTypes` | B listens for `TestEvent` alone (`ClearAllHandlers().HandleEvent<TestEvent, TestEventListener>()`); A publishes `FireAndForgetEvent`, then `TestEvent`, over the same link | Once B's listener ran, B's app event queue has `messagesAdded` 1: its `JMSType IN (...)` filter kept out the type B does not subscribe |
| 11.9 | `ActiveMQReceiverPoolShould.DeliverAPooledEventOnce` | B has a pool `events` handling `TestEvent`; A publishes `TestEvent` | Once B's listener ran, the pool's event queue has `messagesAdded` 1 and B's app event queue 0 (so no second copy can follow), and the listener ran once |
| 11.10 | `ActiveMqDeadLetterShould.RouteAFailedEventToTheDeadLetterAddressWithoutAConduitDlq` | A publishes `ThrowingTestEvent`, B listens with no dead-letter store | Once the broker's `DLQ` holds one message whose `_AMQ_ORIG_QUEUE` is B's app queue, the listener has run exactly 10 times (Artemis's default `max-delivery-attempts`): each abandon was a counted `modified` (`delivery-failed`), never an uncounted `released` |
| 11.11 | `ActiveMqDeadLetterShould.ReplayAPooledEventToItsPoolQueue` | B (dead-letter store) has a pool handling `ThrowingTestEvent`; A publishes it | The captured entry's replay is recaptured and the listener ran twice: the replay went to the pool's queue by fully qualified name (the app queue's filter drops the type, also when sent by FQQN) |
| 11.12 | `ActiveMQReconnectShould.ResumeAfterBrokerRestart` | A sends (15 s `ResponseTimeout`), B handles; one round trip, then the broker container is restarted (`ActiveMQTestBase.RestartBrokerAsync`: Testcontainers `StopAsync`/`StartAsync`, then an AMQP connect probe until it answers; Inconclusive for a configured or reused broker, or a re-mapped port) | Once both transports complete `NextReconnection` (no fixed sleep), a request gets its reply and an event reaches B: the topology (Test-mode queues are not durable), senders, reply queue and consumers were rebuilt on new connections |
| 11.13 | `ActiveMQReconnectShould.FailARequestAwaitingItsReplyWhenTheConnectionIsLost` | A (30 s `ResponseTimeout`) sends a `ShutdownProbeCommand` (5 s handler), B handles; once the handler runs, the broker is stopped | A's request throws exactly `InvalidOperationException("The connection to the broker for endpoint 'broker' was lost before the reply arrived.")` within 15 s of the stop, not at its timeout |
| 11.14 | `ActiveMQReconnectShould.StopWhileReconnecting` | B; the broker is stopped and B's transport reports `IsConnected` false | Disposing B takes under 5 s, and the reconnection it overtook installs nothing (`IsConnected` false, `NextReconnection` not completed) |
| 11.15 | `ActiveMQReplySendersShould.CapTheCachedSendersAndEvictTheLeastRecentlyUsed` | No hosts: an `ActiveMQReplySenders` on its own connection; 64 distinct reply addresses (each lease released at once), the first used again, then 6 more | The cache never holds more than 64 (`Count`); the 6 least recently used (the second to the seventh address) have their links closed, the first keeps its open link, and the 6 new ones are open |
| 11.16 | `ActiveMQReplySendersShould.NeverEvictALinkWithASendInFlight` | No hosts; 64 cached links, the least recently used one leased (a send in flight), then a new address | The leased link stays open; the next least recently used, idle, link is evicted and closed; 64 cached |
| 11.17 | `ActiveMQReplySendersShould.SendThroughALinkOfItsOwnWhenEveryCachedLinkIsBusy` | No hosts; 64 cached links, all leased; a requester's real temporary reply queue | The reply goes out on an uncached link (`Lease.IsCached` false) and arrives; that link is closed on release; 64 cached, none of the leased links closed |
| 11.18 | `ActiveMQReplySendersShould.CloseADiscardedLinkOnlyOnceItsSendsDrain` | No hosts; two leases on one requester's link, the first discarded (a failed send) and released | The link stays open until the second lease is released, then closes; the next lease for that address gets a fresh link |
| 11.19 | `ActiveMQEventRoutingShould.DeliverAnEventTypeAddedSinceTheQueueWasCreated` | B's first release (listens for `TestEvent`) starts and stops; its second release, same app name and prefix, also listens for `FireAndForgetEvent`; A publishes `FireAndForgetEvent` | B's second release receives it, and the existing queue's filter names both types: the filter was updated on start. Disabling the update makes it fail |

#### Amazon SQS

These read what the transport created through an SQS or SNS client on the same LocalStack (`SqsTestBase.CreateSqsClient`/`CreateSnsClient`).

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 11.20 | `SqsCompetingInstanceShould.DeliverRepliesToTheInstanceThatAsked` | B handles; A1 and A2, two instances of one sending application (10 s `ResponseTimeout`), each send 10 `TestQuery`s at once | Every query gets its own answer on the instance that asked it (with one reply queue per application, half the replies went to the other instance, were deleted, and their requests timed out); two `{prefix}-conduit-{A}-replies-` queues exist, and none once A1 and A2 are disposed |
| 11.21 | `SqsTopologyShould.CreateADeadLetterQueueWithARedrivePolicy` | B (`MaxReceiveCount` 2, 2 s visibility timeout, a pool) listens with no dead-letter store; A publishes `ThrowingTestEvent` | B's app queue and pool queue each carry a `RedrivePolicy` naming their own `{queue}-dlq` with `maxReceiveCount` 2; the reply queue and the dead-letter queues carry none; the event arrives in the app queue's dead-letter queue with its `Type` attribute, after the listener ran exactly twice |
| 11.22 | `SqsTopologyShould.ApplyChangedAttributesToAnExistingQueueAndSubscription` | B's first release (listens for `TestEvent`, 2 s visibility timeout) starts and stops; its second release, same app name and prefix, sets a 10 s visibility timeout and `MaxReceiveCount` 7 and also listens for `FireAndForgetEvent`; A publishes `FireAndForgetEvent` | B's second release receives it (LocalStack may first hand it to the first release's cancelled long poll, hence the short visibility timeouts), and the existing queue reads back `VisibilityTimeout` 10 and `maxReceiveCount` 7, its subscription a filter policy of both types and raw delivery. Before the fix the second start failed with `QueueNameExists`. LocalStack does not refuse a re-subscription with different attributes, so the SNS side's refusal path is not exercised here |

### Totals (verified 2026-11-10)

Each transport's `Integration/` directory contains a baseline of **38 `[TestMethod]` behavior tests** (the same shared matrix across all four broker transports: RabbitMQ, Azure Service Bus, Amazon SQS, ActiveMQ). That is the 28 routing/context/pool/topology-mode methods above (categories 1–7, with topology-mode category 7.4 present only where applicable) plus the 6 dead-letter methods in category 8, the 2 self-published/wire-correlation methods in category 9 and the 2 shutdown methods in category 10. Category 4 (competing instances) further multiplies via `[DataRow(1)]`/`[DataRow(20)]`, so the executed case count is higher than the method count. SQS adds **1 reaper test** (`AmazonSqsTopologyReaperShould`) in a separate test class and its **3 transport-specific tests** of category 11, bringing its total to **42**. RabbitMQ adds the **4 transport-specific tests** of category 11, bringing its total to **42**. Azure Service Bus adds its **3**, bringing its total to **41**. ActiveMQ adds its **12**, bringing its total to **50**.

Behavior-test methods across the four transports: 42 (RabbitMQ) + 41 (ASB) + 42 (SQS) + 50 (ActiveMQ) = **175 `[TestMethod]` integration tests**.

---

## Matrix 2: Topology Manager Unit Tests

Unit tests validating that each transport's topology manager creates the correct broker resources with correct names, routing semantics, and durability settings. Tests live in `Unit/TopologyManagerShould.cs` within each transport's test project.

These tests verify naming logic and configuration without requiring a live broker. They test the topology manager's resource plan — the names it computes and the flags it sets. (One exception: ActiveMQ's TM-S.12 reads the flags back from the real broker, since Artemis holds a queue's auto-delete settings only once it created the queue. ActiveMQ's TM-4.1–4.3 test `ActiveMQTopologyManager.QueueLifetimeFor` under the names `ProductionModeDurableNoAutoDelete`, `DeveloperModeAutoDeleteFourHours` and `TestModeAutoDeleteFiveMinutes`.)

### Category TM-1: Resource Naming

| # | Test Method | Validates |
|---|---|---|
| TM-1.1 | `UseCorrectMaxLengthForBroker` | Passes correct maxLength to `TopologyNaming` (RMQ=255, ASB=260/50, SQS=80/256, ActiveMQ=256) |
| TM-1.2 | `UseSharedResourceNameForMainTopic` | Main topic/exchange/address uses `TopologyNaming.CreateSharedResourceName(options, maxLen)` |
| TM-1.3 | `UseAppResourceNameForAppQueue` | Per-app queue uses `TopologyNaming.CreateAppResourceName(options, maxLen)` |
| TM-1.4 | `UseAppResourceNameForReplyQueue` | Reply resource uses `TopologyNaming.CreateAppResourceName(options, "replies", maxLen)` (RabbitMQ and Amazon SQS: one per transport instance, `"replies-{instanceId}"`, through `RabbitMQTopologyManager.ReplyQueueName` and `AmazonSqsTopologyManager.ReplyQueueName`) |
| TM-1.5 | `UseSharedResourceNameForCommandQueues` | Command/query queue names use `TopologyNaming.CreateSharedResourceName(options, type, maxLen)` — shared across apps (unicast) |
| TM-1.6 | `UseAppResourceNameForEventQueues` | Event queue names use `TopologyNaming.CreateAppResourceName(options, type, maxLen)` — per-app (multicast) |
| TM-1.7 | `TruncateLongNamesWithHash` | Names exceeding maxLength keep `maxLength - 9` characters, then `-` and 8 hex characters of an xxHash64 of the whole name (the exact format is pinned once, in `TopologyNamingShould.KeepLongSiblingNamesDistinct`) |
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
| TM-S.1b | `NameTheDeadLetterExchangeAndQueueAfterTheSystem` | `RabbitMQTopologyManager.DeadLetterName` is `{system}-dead-letter` under the topology prefix, with no application name |

#### Azure Service Bus

| # | Test Method | Validates |
|---|---|---|
| TM-S.2 | `CreateSqlFilterOnSubscriptions` | Subscriptions have SQL filter: `Subject = '{FullTypeName}'` |
| TM-S.3 | `CreateSessionEnabledReplyQueue` | `CreateReplyQueueOptions()` sets `RequiresSession = true` (and `CreateSessionEnabledReplyQueueWithCorrectName`: under the reply queue's name) |
| TM-S.4 | `ExpireOrphanedRepliesOnTheReplyQueue` | `CreateReplyQueueOptions()`: `DefaultMessageTimeToLive` = 2 × `ResponseTimeout` |
| TM-S.4b | `AsbReplyShould.CarryATimeToLive` (`Unit/AsbReplyShould.cs`) | The reply itself (`CreateReplyMessage`): the request's `ReplyToSessionId` as its session, and `TimeToLive` = 2 × `ResponseTimeout` |
| TM-S.4c | `AsbReplyShould.ExpireAfterTwiceTheResponseTimeoutAndNoSoonerThanASecond` | The reply's `TimeToLive` and the reply queue's `DefaultMessageTimeToLive` alike: 300 ms and 500 ms give 1 s (Service Bus's minimum), 60 s gives 120 s |
| TM-S.4d | `AsbReplyShould.CarryNoTimeToLiveWithoutAFiniteResponseTimeout` | `Timeout.InfiniteTimeSpan`, zero and `TimeSpan.MaxValue` set no time to live on either (both stay `TimeSpan.MaxValue`), and building them does not throw |

#### Amazon SQS (+SNS)

| # | Test Method | Validates |
|---|---|---|
| TM-S.7 | `CreateSnsFilterPolicy` | SNS subscription filter policy: `{"Type": ["FullTypeName"]}` |
| TM-S.8 | `EnableRawMessageDelivery` | Raw message delivery enabled on all SNS subscriptions |
| TM-S.9 | `SetSqsQueuePolicy` | SQS queue policy allows SNS topic to send messages |
| TM-S.9b | `RefuseAFilterPolicyOverTheSnsLimit` | `AmazonSqsTopologyManager.FilterPolicy`: 150 types make a policy; 151 throw `InvalidOperationException` naming the queue and the count, before AWS is called |
| TM-S.9c | `NameEachDeadLetterQueueAfterItsQueueWithinTheSqsLimit` | `DeadLetterQueueName` is `{queue}-dlq`, and an app or pool queue name is cut short enough (76) that it stays within 80 characters |

#### ActiveMQ Artemis

| # | Test Method | Validates |
|---|---|---|
| TM-S.10 | `CreateSeparateAnycastAndMulticastAddresses` | Two addresses: anycast for commands/queries, multicast for events |
| TM-S.11 | `UseAmqpFilterOnQueues` | `ActiveMQTopologyManager.CommandQueueFilter`: `JMSType='{FullTypeName}'`; `EventQueueFilter`: `JMSType IN ('{FullTypeName}', ...)` (and `WriteTheSameEventFilterForAnyOrderOfTheSameTypes`: each type once, in ordinal order, so the same set read back compares as unchanged) |
| TM-S.12 | `SetAutoDeleteDelayForEphemeralQueues` | `[DataRow]` Developer (14,400,000 ms) and Test (300,000 ms): `EnsureTopologyAsync` against the real broker, then each queue's configuration read back through the management API is non-durable, `auto-delete` true, `auto-delete-delay` the mode's, `auto-delete-message-count` -1. The one container-backed test in `Unit/` (`ActiveMQTestBase.RequireBroker`, Inconclusive without a broker) |
| TM-S.13 | `UseUniqueLinkNames` | `ConsumerLinkName()` gives `consumer-{32 hex}`, unique per link, whatever the queue is called (a name cut from the queue's name to 50 characters made two long names equal and threw for a short one) |

### Totals (verified 2026-06-09; RabbitMQ, Azure Service Bus, Amazon SQS and ActiveMQ 2026-11-10)

The 16 universal topology-manager tests are TM-1 (8) + TM-2 (2) + TM-3 (3) + TM-4 (3). Each transport adds a varying number of transport-specific (TM-S) tests, so the actual `Unit/TopologyManagerShould.cs` `[TestMethod]` count differs per transport:

| Transport | Unit topology `[TestMethod]` count |
|---|---|
| RabbitMQ | 24 |
| Azure Service Bus | 26 |
| Amazon SQS | 19 |
| ActiveMQ | 22 |

(The TM-S.x rows in this document are illustrative; the per-transport tables have grown past the original 12 transport-specific cases. The 16 universal tests are the required floor every new transport must implement.)

---

## Matrix 3: Registration and Configuration Unit Tests

Container-free unit tests over a `ServiceCollection`, proving that a host with **two endpoints on
the same transport** gets two independent transports, and that each endpoint's options bind from its
own configuration section. Tests live in `Unit/` within each transport's test project. No broker is
involved — if one of these needs a container, it is testing the wrong thing.

### Category R-1: Per-endpoint registration (`Unit/{Name}EndpointRegistrationShould.cs`)

Universal. Two endpoints on the same transport, each given a different connection string or region.

| # | Test Method | Assert |
|---|---|---|
| R-1.1 | `GiveEachEndpointItsOwnOptions` | The keyed `{Name}Options` for each endpoint name are different instances, each carrying that endpoint's values and its own `EndpointName` |
| R-1.2 | `ResolveADistinctTransportPerEndpoint` | The keyed `IConduitTransport` for each endpoint name are two distinct objects |
| R-1.3 | `RegisterOneBroadcastTargetAndOneLifecyclePerEndpoint` | `GetServices<IConduitTransport>()` and `GetServices<IHostedService>()` each yield one **distinct** object per endpoint — not the same instance twice (RabbitMQ splits this into `BroadcastToOneTransportInstancePerEndpoint` + `StartOneHostedServicePerEndpoint`) |

These exist because the non-keyed registration they replaced failed silently in every one of these
ways — see [Per-endpoint transport instances](transports/index.md#per-endpoint-transport-instances).

### Category C-1: Configuration binding (`Unit/{Name}ConfigurationShould.cs`)

Universal for any transport with an options class. Build a host from in-memory configuration and
assert the resolved keyed options. These must not start hosted services.

| # | Test Method | Assert |
|---|---|---|
| C-1.1 | `BindOptionsFromTheEndpointSection` | Scalars under `Conduit:Endpoints:{name}` land on the options instance |
| C-1.2 | `GiveTwoEndpointsOnTheSameTransportTheirOwnKeyedOptions` | Two endpoints bind from their own sections, not each other's |
| C-1.3 | `LetTheExplicitArgumentOverrideConfiguration` | A value passed to `Use{Name}(...)` or set in the `configure` callback beats the configured one |
| C-1.4 | `MergeAConfiguredPoolWithCodeDeclaredMessageTypes` | A pool named in both places is one pool holding the union of both sources' message types |
| C-1.5 | `ExplainWhichKeyIsMissingWhen...` | A required value supplied by neither source throws `InvalidOperationException` naming both the configuration key and the `Use*` argument |

SignalR and HTTP have one key each and no pools, so they implement C-1.1 and C-1.5 only, named for
the key they bind (`ResolveATransportFromTheConfiguredHubUri` / `...BaseAddress`). RabbitMQ carries
the full C-1.x set; ActiveMQ and Azure Service Bus bind through the identical shared path and
currently lean on that plus the core binder tests, but a **new** transport should write C-1.x
directly rather than assume the shared path covers it.

### Category C-S: Transport-specific binding

Only where a transport hand-maps something the binder cannot handle:

| # | Test Method | Transport | Validates |
|---|---|---|---|
| C-S.1 | `BindRegionCredentialsAndScalars` | Amazon SQS | `Region` → `RegionEndpoint`, `Credentials:AccessKey`/`SecretKey` → `BasicAWSCredentials`, and the scalars (`VisibilityTimeoutSeconds`, `MaxReceiveCount`, `ServiceUri`) |
| C-S.2 | `BuildSessionCredentialsWhenATokenIsSupplied` | Amazon SQS | A `SessionToken` selects `SessionAWSCredentials` |
| C-S.3 | `LeaveCredentialsNullForTheDefaultAwsChain` | Amazon SQS | No key pair leaves `Credentials` null so the AWS default chain applies |

### Category O-S: Transport-specific option validation (`Unit/{Name}OptionsShould.cs`)

Where a transport refuses option values its broker cannot represent, at `Use{Name}(...)`:

| # | Test Method | Transport | Validates |
|---|---|---|---|
| O-S.1 | `RejectConcurrencyOutsideUShort` | RabbitMQ | A `DefaultConcurrency` or pool `Concurrency` of 0, -1 or 65536 throws `ArgumentOutOfRangeException` naming the setting (and the pool), with the value as `ActualValue` |
| O-S.2 | `AcceptConcurrencyAtTheUShortBounds` | RabbitMQ | 1 and 65535 are accepted, for both |

Core-side coverage for the shared machinery lives in `tests/Conduit.Tests/Unit/`:
`ConduitConfigurationShould` (core scalar binding, enum/`TimeSpan` parsing, absent keys, code wins,
`Conduit:Endpoints` not leaking into `ConduitOptions.Endpoints`, the generated
`AddConduit(IConfiguration, …)` overload), `ReceiverPoolShould` (`AddPool` get-or-create, `Handle`
overload equivalence and de-duplication), and `TransportOptionsBindingShould`
(`ConduitConfigurationBinder`: pool merge, excluded keys, missing/duplicate pool `Name`).

---

## Adding a New Transport

When adding a new broker transport, create a test project and implement:

1. **All transport behavior tests** (Matrix 1 — 36 `[TestMethod]` tests per existing transport, categories 1–10) — copy the test class structure from an existing transport, adjust the fixture
2. **All 16 universal topology manager tests** (Matrix 2, TM-1.x through TM-4.x) — verify resource naming, routing semantics, pool routing, and topology modes
3. **Transport-specific topology tests** (Matrix 2, TM-S.x) — add tests for broker-specific features
4. **Per-endpoint registration and configuration binding tests** (Matrix 3, R-1.x and C-1.x) — container-free `ServiceCollection` tests; R-1.x is non-negotiable, a transport that skips it can ship the silent shared-singleton bug all broker packages once had

### Test Project Structure

```
tests/Conduit.Transport.{Name}.Tests/
    Conduit.Transport.{Name}.Tests.csproj
    Support/
        Contracts/          # 18 contract types (source gen requires per-assembly)
        Handlers/           # 16 handler/listener types
    Integration/
        {Name}Fixture.cs    # Extends TransportTestFixture, manages container
        {Name}CommandRoutingShould.cs
        {Name}QueryRoutingShould.cs
        {Name}EventRoutingShould.cs
        {Name}CompetingInstanceShould.cs
        {Name}MessageContextShould.cs
        {Name}ReceiverPoolShould.cs
        {Name}TopologyModeShould.cs
        {Name}DeadLetterShould.cs
        {Name}SelfPublishedEventShould.cs
        {Name}ShutdownShould.cs
    Unit/
        TopologyManagerShould.cs
        {Name}EndpointRegistrationShould.cs
        {Name}ConfigurationShould.cs
```

### Source Gen Constraint

Contracts and handlers MUST live in the test project assembly — the source generator discovers types per compilation unit. They cannot be shared via a library reference. Copy from an existing transport's `Support/` directory and adjust the namespace.
