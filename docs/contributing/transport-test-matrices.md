# Transport Test Matrices

Definitive test matrices for broker transport implementations. Every new transport MUST pass all universal tests. Transport-specific tests are added as needed for broker-specific features.

Each transport has its own test project: `tests/Conduit.Transport.{Name}.Tests/` with `Integration/` for behavior tests and `Unit/` for the container-free topology manager, registration and configuration tests.

---

## Matrix 1: Transport Behavior Tests

Multi-host integration tests validating message routing correctness. Each test spins up 2-3 in-process `TestHost` instances connected to the same broker. Tests live in `Integration/` within each transport's test project.

### Test Infrastructure

Each transport test project contains:
- `Support/Contracts/` — 19 contract types (commands, queries, events, results, F&F types, the shutdown probe) discovered by the source generator (RabbitMQ: 21, adding the `ConcurrencyProbeCommand` and `ThrowingFireAndForgetCommand` of its [transport-specific](#category-11-transport-specific-behavior) tests)
- `Support/Handlers/` — 17 handlers/listeners recording invocations via `SpyLog` (RabbitMQ: 20, adding the handlers of those two commands and the `ConcurrencyProbeMark` the probe handler records)
- `Integration/{Prefix}TestBase.cs` — the abstract base every integration test class derives from: `RabbitMqTestBase`, `AsbTestBase`, `SqsTestBase`, `ActiveMQTestBase`. It owns the broker container as static state (Testcontainers), offers `CreateHostAsync`, `CreateSenderHostAsync`, `CreateHostWithDeadLettersAsync` and `UniqueAppName`, and its `[TestInitialize]` marks a test Inconclusive when the broker is unavailable
- `Integration/AssemblySetup.cs` — `[AssemblyInitialize]`/`[AssemblyCleanup]` delegating to the base class's static `InitializeContainer`/`CleanupContainer`

`{Transport}` in a test class name below is that project's prefix: `RabbitMq`, `Asb`, `Sqs` or `ActiveMQ` (ActiveMQ's dead-letter class is `ActiveMqDeadLetterShould`).

Shared infrastructure in `Conduit.TestUtils`:
- `SpyLog` — thread-safe invocation recorder with `WaitForCountAsync`
- `MessageContextSnapshot` — captures `MessageId`, `CausationId`, `ConversationId`
- `TestHost` — lightweight `IHost` wrapper that starts hosted services; every test disposes its hosts with `await using`
- `TestTopologyPrefix` — the per-project Test topology prefix `test-{hash8(assembly)}-{random}`
- `FakeBrokerTransport` — an in-memory `BrokerTransportBase` double for container-free tests in `Conduit.Tests`

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
| 3.4 | `DeliverFireAndForgetEventToSubscribingApp` | A publishes `[FireAndForget]` event, B listens | B spy == 1 (F&F only affects local listeners, transport broadcast unchanged) |

What a broker does with an event whose listener throws (capture, dead letter, redelivery) is category 8's, and for each broker's own dead-letter mechanism, category 11's.

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
| 7.2 | `CreateEphemeralResourcesInDeveloperMode` | A + B with `TopologyMode.Developer` | Host starts, resources have auto-delete/TTL flags (Amazon SQS: the host starts; SNS and SQS have no auto-delete to assert) |
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

Universal — required of every transport, real broker only (the in-memory `FakeBrokerTransport` double in `Conduit.TestUtils` already covers this at the unit level; these two prove the same behavior actually holds on the wire).

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 9.1 | `RunLocalListenersOnceForASelfPublishedEvent` | Single host both publishes and locally listens to `TestEvent` | Publish an event the app also listens to; assert one listener execution, not two — wait deliberately for the broker's own-copy round trip before asserting, since a bare `await` on publish passes vacuously either way |
| 9.2 | `CarryKnownCorrelationAndTraceContextAcrossTheWire` | A publishes `ContextCaptureEvent` under a known `MessageContext` and `Activity`, B captures | Publish under a known MessageContext and Activity; assert MessageId, CausationId, ConversationId and traceparent arrive intact |

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
| 11.3b | `RabbitMqDeadLetterShould.CaptureAFailedFireAndForgetCommand` | A (producer-only) routes `ThrowingFireAndForgetCommand`, whose handler throws, to B, which has a dead-letter store; once it is captured a standby host joins the command's queue and B is disposed | The store holds one entry for the command, with the `Reference` its failure was logged under; B's handler ran once; 2 s after B stopped the standby has run nothing (a delivery B left unsettled would have been requeued to it as B's channel closed: skipping the post-capture acknowledgement makes it fail), and neither the command's queue nor `{prefix}-conduit-dead-letter` holds a message |
| 11.3c | `RabbitMqDeadLetterShould.RouteAFailedFireAndForgetCommandToTheDeadLetterExchangeWithoutAConduitDlq` | A (producer-only) routes `ThrowingFireAndForgetCommand` to B, which has no dead-letter store | After 2 s the handler has run once (rejected, neither acknowledged nor requeued), and `{prefix}-conduit-dead-letter` holds the command once, with `x-first-death-reason: rejected` and `x-first-death-queue` = the command's shared queue |
| 11.4 | `RabbitMqReplyQueueShould.FallBackToAFreshNameWhenTheNameIsLocked` | B handles; a test connection declares an exclusive queue under the reply queue name A would take (`InitialReplyQueueInstanceId` pins it); then A starts and sends | A starts despite `RESOURCE_LOCKED`, its request gets its reply (so the reply went to A's fresh queue), and the locked queue holds no message |
| 11.4b | `RabbitMqDurabilityShould.KeepAQueuedEventAcrossABrokerRestart` | B (Production topology under a `TestTopologyPrefix` prefix, listens for `TestEvent` alone) starts and stops; A (producer-only) publishes `TestEvent`; the broker container is restarted (`RequireRestartableBroker` first, then `RestartBrokerAsync`); B starts again | B's listener receives the event: the persistent message survived in the durable queue. The test deletes its durable topology in a `finally` |
| 11.4c | `RabbitMqDurabilityShould.PublishRequestsPersistent` | B handles, A sends `TestCommand`; a test queue bound to the exchange takes a copy | The copy's `BasicProperties.Persistent` is true |
| 11.4d | `RabbitMqDurabilityShould.PublishRepliesPersistent` | B handles; a raw requester publishes `TestCommand` with `ReplyTo` = its own exclusive queue | The reply's `BasicProperties.Persistent` is true |

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
| 11.19b | `ActiveMQDurabilityShould.KeepAQueuedEventAcrossABrokerRestart` | As 11.4b, on Artemis (`ActiveMQTestBase.RequireRestartableBroker`, then `RestartBrokerAsync`) | B receives the event: the durable-header message survived in the durable queue. The test deletes both addresses, forced, in a `finally` (`ArtemisManagement.DeleteAddressAsync`) |
| 11.19c | `ActiveMQDurabilityShould.SendRepliesDurable` | B handles; a raw AMQPNetLite requester (a dynamic reply node) sends `TestCommand` to the commands address | The reply's `Header.Durable` is true |

#### Amazon SQS

These read what the transport created through an SQS or SNS client on the same LocalStack (`SqsTestBase.CreateSqsClient`/`CreateSnsClient`).

| # | Test Method | Hosts | Assert |
|---|---|---|---|
| 11.20 | `SqsCompetingInstanceShould.DeliverRepliesToTheInstanceThatAsked` | B handles; A1 and A2, two instances of one sending application (10 s `ResponseTimeout`), each send 10 `TestQuery`s at once | Every query gets its own answer on the instance that asked it (with one reply queue per application, half the replies went to the other instance, were deleted, and their requests timed out); two `{prefix}-conduit-{A}-replies-` queues exist, and none once A1 and A2 are disposed |
| 11.21 | `SqsTopologyShould.CreateADeadLetterQueueWithARedrivePolicy` | B (`MaxReceiveCount` 2, 2 s visibility timeout, a pool) listens with no dead-letter store; A publishes `ThrowingTestEvent` | B's app queue and pool queue each carry a `RedrivePolicy` naming their own `{queue}-dlq` with `maxReceiveCount` 2; the reply queue and the dead-letter queues carry none; the event arrives in the app queue's dead-letter queue with its `Type` attribute, after the listener ran exactly twice |
| 11.22 | `SqsTopologyShould.ApplyChangedAttributesToAnExistingQueueAndSubscription` | B's first release (listens for `TestEvent`, 2 s visibility timeout) starts and stops; its second release, same app name and prefix, sets a 10 s visibility timeout and `MaxReceiveCount` 7 and also listens for `FireAndForgetEvent`; A publishes `FireAndForgetEvent` | B's second release receives it (LocalStack may first hand it to the first release's cancelled long poll, hence the short visibility timeouts), and the existing queue reads back `VisibilityTimeout` 10 and `maxReceiveCount` 7, its subscription a filter policy of both types and raw delivery. A second start that re-created the queue instead would fail with `QueueNameExists`. LocalStack does not refuse a re-subscription with different attributes, so the SNS side's refusal path is not exercised here |
| 11.23 | `SqsShutdownShould.HandBackReceivedButUnstartedMessagesAtOnce` | B1 (`DefaultConcurrency` 1, 60 s visibility timeout); five 3 s `ShutdownProbeCommand`s sent in one batch straight to its app queue; B1 stopped 0.5 s into the first, its poller blocked on the slot (not in a long poll); then B2, same app name and prefix | B2 receives the other four within 10 s, each once, and B1 ran only the first: the stopping poller made the rest of its batch visible at once. Without that call B2 receives none within 10 s |

### Totals

Each transport's `Integration/` directory contains a baseline of **38 `[TestMethod]` behavior tests** (the same shared matrix across all four broker transports: RabbitMQ, Azure Service Bus, Amazon SQS, ActiveMQ). That is the 28 routing/context/pool/topology-mode methods above (categories 1–7, with topology-mode category 7.4 present only where applicable) plus the 6 dead-letter methods in category 8, the 2 self-published/wire-correlation methods in category 9 and the 2 shutdown methods in category 10. Category 4 (competing instances) further multiplies via `[DataRow(1)]`/`[DataRow(20)]`, so the executed case count is higher than the method count. SQS adds **1 reaper test** (`AmazonSqsTopologyReaperShould`) in a separate test class and its **4 transport-specific tests** of category 11, bringing its total to **43**. RabbitMQ adds the **9 transport-specific tests** of category 11, bringing its total to **47**. Azure Service Bus adds its **3**, bringing its total to **41**. ActiveMQ adds its **14**, bringing its total to **52**.

Behavior-test methods across the four transports: 47 (RabbitMQ) + 41 (ASB) + 43 (SQS) + 52 (ActiveMQ) = **183 `[TestMethod]` integration tests**.

---

## Matrix 2: Topology Manager Unit Tests

Unit tests validating that each transport's topology manager creates the correct broker resources with correct names, routing semantics, and durability settings. Tests live in `Unit/TopologyManagerShould.cs` within each transport's test project.

These tests verify naming logic and configuration without requiring a live broker. They test the topology manager's resource plan — the names it computes and the flags it sets. (One exception: ActiveMQ's TM-S.12 reads the flags back from the real broker, since Artemis holds a queue's auto-delete settings only once it created the queue.)

The `Test Method` column of TM-1 to TM-4 names the row. Each `TopologyManagerShould` marks the rows it covers with a
`// --- TM-x.y: Name ---` comment above the tests, which are named for the transport's own resources (RabbitMQ's
TM-1.2 is `UseTopologyNamingForExchangeName`, Amazon SQS's `UseSharedResourceNameForSnsTopic`). See
[Universal rows per transport](#universal-rows-per-transport) for the rows that do not apply to a broker, or that
its unit tests do not cover.

### Category TM-1: Resource Naming

| # | Test Method | Validates |
|---|---|---|
| TM-1.1 | `UseCorrectMaxLengthForBroker` | Passes correct maxLength to `TopologyNaming` (RMQ=255, ASB=260/50, SQS=80 reply / 76 app and pool, so `-dlq` fits / 256 topic, ActiveMQ=256) |
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

### Universal rows per transport

RabbitMQ and Azure Service Bus cover all 16 rows. Amazon SQS and ActiveMQ differ.

**Amazon SQS** has no shared per-type queues: each application has one queue (and one per pool) whose SNS filter policy
lists the types it takes, so a command has one consumer because only one application's filter names it.

- *Does not apply:* TM-1.5, TM-1.6 and TM-2.1 (no per-type queues), and TM-4 (SNS and SQS have no auto-delete;
  Developer and Test resources are reaped by prefix instead, 7.4).
- *Not covered at unit level:* TM-2.2. It applies, since an event's subscription is on each listening application's
  own queue, but no unit test pins it.
- TM-1.1 is `UseCorrectMaxLengthForSqsQueues`.

**ActiveMQ** names each application's event queue on the multicast address `CreateAppResourceName(options, 256)` (in
`ActiveMQTopologyManager`), and has no named reply queue: each instance's is a dynamic node the broker creates and names.

- *Does not apply:* TM-1.4 (the dynamic reply node), and TM-1.6's per-type event queues, which ActiveMQ does not have.
- *Covered under other markers:* the per-app event queue follows TM-1.3, pinned by `UseAppResourceNameForEventQueues`
  under the TM-1.6 marker. The 256-character limit (TM-1.1) is pinned by the TM-1.7 tests. TM-2 is covered under TM-1.5
  and TM-1.6 (`CommandQueueNamesAreIdenticalAcrossApps`, `EventQueueNamesAreDifferentAcrossApps`), TM-3.1 by the pool
  queue names (`UseAppResourceNameForPoolEventQueues`, `UseSharedResourceNameForPoolCommandQueues`), and TM-4.1–4.3 by
  tests of `ActiveMQTopologyManager.QueueLifetimeFor` (`ProductionModeDurableNoAutoDelete`,
  `DeveloperModeAutoDeleteFourHours`, `TestModeAutoDeleteFiveMinutes`).
- *Not covered at unit level:* TM-3.2 and TM-3.3. They apply: the app event queue's filter takes the subscribed events
  minus every pool-assigned type. No unit test pins that; integration test 11.9 does.

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
| TM-S.2 | `CreateSqlFilterWithCorrectFormat`, `CreateSqlFilterMatchingTopologyManagerFormat` | Subscriptions have the SQL filter `sys.Label = '{FullTypeName}'` (a Service Bus SQL filter reads the message's `Subject` as `sys.Label`) |
| TM-S.3 | `CreateSessionEnabledReplyQueue` | `CreateReplyQueueOptions()` sets `RequiresSession = true` (and `CreateSessionEnabledReplyQueueWithCorrectName`: under the reply queue's name) |
| TM-S.4 | `ExpireOrphanedRepliesOnTheReplyQueue` | `CreateReplyQueueOptions()`: `DefaultMessageTimeToLive` = 2 × `ResponseTimeout` |
| TM-S.4b | `AsbReplyShould.CarryATimeToLive` (`Unit/AsbReplyShould.cs`) | The reply itself (`CreateReplyMessage`): the request's `ReplyToSessionId` as its session, and `TimeToLive` = 2 × `ResponseTimeout` |
| TM-S.4c | `AsbReplyShould.ExpireAfterTwiceTheResponseTimeoutAndNoSoonerThanASecond` | The reply's `TimeToLive` and the reply queue's `DefaultMessageTimeToLive` alike: 300 ms and 500 ms give 1 s (Service Bus's minimum), 60 s gives 120 s |
| TM-S.4d | `AsbReplyShould.CarryNoTimeToLiveWithoutAFiniteResponseTimeout` | `Timeout.InfiniteTimeSpan`, zero and `TimeSpan.MaxValue` set no time to live on either (both stay `TimeSpan.MaxValue`), and building them does not throw |

#### Amazon SQS (+SNS)

| # | Test Method | Validates |
|---|---|---|
| TM-S.7 | `CreateSnsFilterPolicy` | SNS subscription filter policy: `{"Type": ["FullTypeName"]}` |
| TM-S.9b | `RefuseAFilterPolicyOverTheSnsLimit` | `AmazonSqsTopologyManager.FilterPolicy`: 150 types make a policy; 151 throw `InvalidOperationException` naming the queue and the count, before AWS is called |
| TM-S.9c | `NameEachDeadLetterQueueAfterItsQueueWithinTheSqsLimit` | `DeadLetterQueueName` is `{queue}-dlq`, and an app or pool queue name is cut short enough (76) that it stays within 80 characters |

Raw message delivery on the subscriptions is read back from LocalStack by 11.22, not by a unit test. The queue policy that lets the topic send to each queue has no test of its own; every integration test that delivers a message depends on it.

#### ActiveMQ Artemis

| # | Test Method | Validates |
|---|---|---|
| TM-S.10 | `CreateSeparateAnycastAndMulticastAddresses` | Two addresses: anycast for commands/queries, multicast for events |
| TM-S.11 | `UseAmqpFilterOnQueues` | `ActiveMQTopologyManager.CommandQueueFilter`: `JMSType='{FullTypeName}'`; `EventQueueFilter`: `JMSType IN ('{FullTypeName}', ...)` (and `WriteTheSameEventFilterForAnyOrderOfTheSameTypes`: each type once, in ordinal order, so the same set read back compares as unchanged) |
| TM-S.12 | `SetAutoDeleteDelayForEphemeralQueues` | `[DataRow]` Developer (14,400,000 ms) and Test (300,000 ms): `EnsureTopologyAsync` against the real broker, then each queue's configuration read back through the management API is non-durable, `auto-delete` true, `auto-delete-delay` the mode's, `auto-delete-message-count` -1. The one container-backed test in `Unit/` (`ActiveMQTestBase.RequireBroker`, Inconclusive without a broker) |
| TM-S.13 | `UseUniqueLinkNames` | `ConsumerLinkName()` gives `consumer-{32 hex}`, unique per link, whatever the queue is called (a name derived from the queue's name and cut to 50 characters would make two long names equal and throw for a short one) |

### Totals

The 16 universal topology-manager tests are TM-1 (8) + TM-2 (2) + TM-3 (3) + TM-4 (3). Each transport adds a varying number of transport-specific (TM-S) tests, so the actual `Unit/TopologyManagerShould.cs` `[TestMethod]` count differs per transport:

| Transport | Unit topology `[TestMethod]` count |
|---|---|
| RabbitMQ | 24 |
| Azure Service Bus | 26 |
| Amazon SQS | 19 |
| ActiveMQ | 22 |

The TM-S rows name the transport-specific behaviors; each `TopologyManagerShould` also carries a few tests of its own outside the matrix (`AcceptConduitOptionsInConstructor`, `CreateAppQueueWithoutSessions` and the like). The 16 universal tests are the floor a new transport must implement, unless a row cannot apply to its broker, as with Amazon SQS and ActiveMQ above.

---

## Matrix 3: Registration and Configuration Unit Tests

Container-free unit tests over a `ServiceCollection`, proving that a host with **two endpoints on
the same transport** gets two independent transports, and that each endpoint's options bind from its
own configuration section; and, where a transport builds its broker clients from those options, over
the clients it builds (category F-S). Tests live in `Unit/` within each transport's test project. No broker is
involved — if one of these needs a container, it is testing the wrong thing.

### Category R-1: Per-endpoint registration (`Unit/{Name}EndpointRegistrationShould.cs`)

Universal. Two endpoints on the same transport, each given a different connection string or region.

| # | Test Method | Assert |
|---|---|---|
| R-1.1 | `GiveEachEndpointItsOwnOptions` | The keyed `{Name}Options` for each endpoint name are different instances, each carrying that endpoint's values and its own `EndpointName` |
| R-1.2 | `ResolveADistinctTransportPerEndpoint` | The keyed `IConduitTransport` for each endpoint name are two distinct objects |
| R-1.3 | `RegisterOneBroadcastTargetAndOneLifecyclePerEndpoint` | `GetServices<IConduitTransport>()` and `GetServices<IHostedService>()` each yield one **distinct** object per endpoint — not the same instance twice (RabbitMQ splits this into `BroadcastToOneTransportInstancePerEndpoint` + `StartOneHostedServicePerEndpoint`) |
| R-1.4 | `RejectANonPositiveOrInfiniteResponseTimeout` | Broker transports. Its `Use*` extension registers through `ConduitEndpointBuilder.UseTransport<TTransport>(BrokerTransportOptions)`, so `Use{Name}(..., options => options.ResponseTimeout = ...)` with zero, a negative value or `Timeout.InfiniteTimeSpan` throws `InvalidOperationException` naming the endpoint |

R-1.1 to R-1.3 pin what a shared, non-keyed registration gets wrong without an error: two endpoints on one transport
package sharing one options instance (the last endpoint's values winning), one transport, or one broadcast target and
lifecycle — see
[Per-endpoint transport instances](../transports/index.md#per-endpoint-transport-instances).
R-1.4 exists because a broker extension that calls `UseTransport<TTransport>()` instead skips the shared
validation, and the bad timeout then fails every request rather than the registration. The four existing
broker transports call the options overload and lean on the core `BrokerTransportOptionsShould` (which
pins the overload's accept and reject sides); a **new** broker transport should write R-1.4 directly.

### Category C-1: Configuration binding (`Unit/{Name}ConfigurationShould.cs`)

Universal for any transport with an options class. Build a host from in-memory configuration and
assert the resolved keyed options. These must not start hosted services.

| # | Test Method | Assert |
|---|---|---|
| C-1.1 | `BindOptionsFromTheEndpointSection` | Scalars under `Conduit:Endpoints:{name}` land on the options instance |
| C-1.2 | `GiveTwoEndpointsOnTheSameTransportTheirOwnKeyedOptions` | Two endpoints bind from their own sections, not each other's |
| C-1.3 | `LetTheExplicitArgumentOverrideConfiguration` | A value passed to `Use{Name}(...)` or set in the `configure` callback beats the configured one |
| C-1.4 | `MergeAConfiguredPoolWithCodeDeclaredMessageTypes` | A pool named in both places is one pool holding the union of both sources' message types |
| C-1.5 | `ExplainWhichKeyIsMissingWhen...` (RabbitMQ: `ExplainWhichKeyIsMissingWhenNoConnectionStringIsSupplied`) | A required value supplied by neither source throws `InvalidOperationException` naming both the configuration key and the `Use*` argument |

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
| C-S.4 | `LetCodeOverrideTheConfiguredRegion` | Amazon SQS | A region set in the `configure` callback beats the configured `Region` (C-1.3 for the hand-mapped key) |

### Category F-S: Transport-specific client construction (`Unit/{Name}ClientFactoryShould.cs`)

Where a transport builds its broker clients from the bound options:

| # | Test Method | Transport | Validates |
|---|---|---|---|
| F-S.1 | `UseConfiguredCredentialsAgainstAws` | Amazon SQS | With `Region` and `Credentials` (a `Support/StubAwsCredentials`), both clients carry those credentials (`Config.DefaultAWSCredentials`), in that region |
| F-S.2 | `UseConfiguredCredentialsWithACustomEndpoint` | Amazon SQS | With `ServiceUri` and `Credentials`, the endpoint is overridden and the credentials are still the configured ones |
| F-S.3 | `UseAPlaceholderPairForACustomEndpointWithoutCredentials` | Amazon SQS | `ServiceUri` alone gets the LocalStack placeholder pair (`test`/`test`) |
| F-S.4 | `PutNoCredentialsOnTheClientsAgainstAwsWithoutConfiguredOnes` | Amazon SQS | `Region` alone puts neither configured nor placeholder credentials on the clients' configuration, so the SDK's default chain applies |

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

1. **All transport behavior tests** (Matrix 1 — 38 `[TestMethod]` tests per existing transport, categories 1–10) — copy the test classes and the `{Prefix}TestBase` from an existing transport, and adjust the base class
2. **All 16 universal topology manager tests** (Matrix 2, TM-1.x through TM-4.x), unless a row cannot apply to the broker ([Universal rows per transport](#universal-rows-per-transport)) — verify resource naming, routing semantics, pool routing, and topology modes
3. **Transport-specific topology tests** (Matrix 2, TM-S.x) — add tests for broker-specific features
4. **Per-endpoint registration and configuration binding tests** (Matrix 3, R-1.x and C-1.x) — container-free `ServiceCollection` tests; R-1.x is non-negotiable: without it a transport can ship a shared singleton that a second endpoint silently overwrites

### Test Project Structure

```
tests/Conduit.Transport.{Name}.Tests/
    Conduit.Transport.{Name}.Tests.csproj
    Support/
        Contracts/          # 19 contract types (source gen requires per-assembly)
        Handlers/           # 17 handler/listener types
    Integration/
        AssemblySetup.cs    # [AssemblyInitialize]/[AssemblyCleanup] -> {Name}TestBase
        {Name}TestBase.cs   # Abstract base: owns the container, host factories, availability guard
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
