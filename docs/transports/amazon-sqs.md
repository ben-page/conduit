# Amazon SQS

- [Overview](#overview)
- [Setup](#setup)
- [Options and configuration keys](#options-and-configuration-keys)
  - [Loading from configuration](#loading-from-configuration)
  - [Credentials](#credentials)
  - [LocalStack](#localstack)
- [Topology](#topology)
  - [Unicast by convention](#unicast-by-convention)
  - [Filter policies and raw message delivery](#filter-policies-and-raw-message-delivery)
  - [Name length](#name-length)
  - [Auto-discovery](#auto-discovery)
- [Permissions](#permissions)
- [Developer and Test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
- [Failure handling](#failure-handling)
  - [Stopping](#stopping)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)
- [Limitations](#limitations)

## Overview

The Amazon SQS transport provides durable, broker-based messaging over SNS and SQS together: **SNS** carries the fan-out, **SQS** holds the per-application queues. All message types (commands, queries, events) are published to a single SNS topic, and SNS subscription filter policies route each message to the queues that should receive it. Commands and queries use request/response with a reply queue per transport instance. The transport runs as an `IHostedService` that manages topology, long-polling consumers, and reply correlation.

Unlike the other broker transports there is no connection string: the transport connects with an AWS region and the default AWS credential chain, unless `Credentials` is set.

## Setup

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("orders", endpoint =>
    {
        endpoint.UseAmazonSqs(options => options.Region = RegionEndpoint.EUWest1);
        endpoint.RouteNamespace<Orders.Commands.CreateOrder>();
    });
});
```

`UseAmazonSqs` registers the transport as both `IConduitTransport` and `IHostedService`. The transport starts when the host starts. The callback is optional when the settings come from configuration.

## Options and configuration keys

```csharp
endpoint.UseAmazonSqs(options =>
{
    options.Region = RegionEndpoint.EUWest1;
    options.VisibilityTimeoutSeconds = 120;   // default: 60
    options.MaxReceiveCount = 3;              // default: 5
});
```

`AmazonSqsOptions`:

| Property | Default | Description |
|---|---|---|
| `Region` | `null` | AWS region. Falls back to the SDK's own region resolution when unset. Not used when `ServiceUri` is set. |
| `ServiceUri` | `null` | Override endpoint for both SNS and SQS, used for [LocalStack](#localstack). A non-LocalStack endpoint override (a VPC or FIPS endpoint) needs explicit `Credentials`, otherwise the placeholder pair is used. |
| `Credentials` | `null` | Explicit AWS credentials, used with or without `ServiceUri`. When unset, the default AWS credential chain applies; with `ServiceUri` set, the LocalStack placeholder pair is used instead (see [Credentials](#credentials) and [LocalStack](#localstack)). |
| `DefaultConcurrency` | `10` | How many handlers the application queue's poller runs at once. The poller receives up to 10 messages per long poll and starts each one as a handler slot frees up. |
| `ResponseTimeout` | `00:01:00` | How long a request waits for its reply (see [Request/response](#requestresponse)). Positive and finite, and at most `VisibilityTimeoutSeconds`. |
| `VisibilityTimeoutSeconds` | `60` | SQS visibility timeout of the application and pool queues. Must be at least `ResponseTimeout`, validated at startup. |
| `MaxReceiveCount` | `5` | How many times SQS delivers a message before moving it to its queue's dead-letter queue (see [Failure handling](#failure-handling)). 1 to 1000, validated at startup. |
| `RawMessageDelivery` | `true` | Required to stay `true` (see [Filter policies and raw message delivery](#filter-policies-and-raw-message-delivery)). |
| `Pools` | empty | Named receiver pools, added with `AddPool` (see [Receiver pools](#receiver-pools)). |
| `EndpointName` | the endpoint's name | Read-only: `UseAmazonSqs` sets it from `AddEndpoint`, never from configuration. |

The transport validates `RawMessageDelivery`, `MaxReceiveCount` and `VisibilityTimeoutSeconds` when the host builds it, and throws `InvalidOperationException` naming the setting.

Topic and queue names are controlled by `SystemName`, `ApplicationName` and `TopologyMode` on `ConduitOptions` (see [Developer and Test modes](#developer-and-test-modes)).

### Loading from configuration

Every property above but `EndpointName` also binds from `Conduit:Endpoints:{endpointName}`, under the same name, so `UseAmazonSqs()` can run with no callback at all:

```json
{
  "Conduit": {
    "Endpoints": {
      "orders": {
        "Region": "eu-west-1",
        "VisibilityTimeoutSeconds": 120,
        "DefaultConcurrency": 20
      }
    }
  }
}
```

```csharp
conduit.AddEndpoint("orders", endpoint => endpoint.UseAmazonSqs());
```

`Region` is written as an AWS **system name** (`"eu-west-1"`), not a .NET constant name. Configuration binds first; a `configure` callback overrides it. See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration) for the full precedence rule, the pool-merge behavior, and the complete key reference.

### Credentials

Without `Credentials`, the transport authenticates through the **default AWS credential chain** (environment variables, profile, container or instance role) against AWS; a `ServiceUri` endpoint without `Credentials` gets the LocalStack placeholder pair (see [LocalStack](#localstack)). The default chain is what you want in a deployed environment rather than secrets in appsettings. Set `Credentials` to use explicit ones, against AWS or a `ServiceUri` endpoint alike. Configuration supplies it nested under `Credentials`, mirroring the property it populates:

```json
{
  "Conduit": {
    "Endpoints": {
      "orders": {
        "ServiceUri": "http://localhost:4566",
        "Credentials": {
          "AccessKey": "AKIA…",
          "SecretKey": "…",
          "SessionToken": "…"
        }
      }
    }
  }
}
```

`SessionToken` is optional: supply it for temporary credentials, omit it for long-lived ones. If either `AccessKey` or `SecretKey` is missing, the transport leaves `Credentials` unset.

### LocalStack

Point both SNS and SQS at a local endpoint with `ServiceUri`:

```json
{
  "Conduit": {
    "Endpoints": {
      "orders": {
        "Region": "us-east-1",
        "ServiceUri": "http://localhost:4566"
      }
    }
  }
}
```

When `ServiceUri` is set, `Credentials` is optional: LocalStack does not validate credentials, but the AWS SDK insists on *a* credential source, so the transport substitutes a placeholder pair when none is configured. Supply real ones only if your local stack checks them.

## Topology

The transport creates the following on startup:

```
SNS topic: {systemName}
SQS queue: {systemName}-{appName}
├── SNS subscription, filter policy on the application's types
├── redrive policy → {systemName}-{appName}-dlq
SQS queue: {systemName}-{appName}-dlq                     dead-letter queue
SQS queue: {systemName}-{appName}-{poolName}
├── SNS subscription, filter policy on the pool's types
├── redrive policy → {systemName}-{appName}-{poolName}-dlq
SQS queue: {systemName}-{appName}-{poolName}-dlq          dead-letter queue
SQS queue: {systemName}-{appName}-replies-{instanceId}    one per transport instance, deleted on stop
```

`{systemName}` is `ConduitOptions.SystemName` (`conduit` by default), prefixed with `{TopologyPrefix}-` in Developer and Test modes, or whenever `TopologyPrefix` is set explicitly.

Every message (command, query or event) is published to the single SNS topic. Each queue has one subscription, whose filter policy lists the types the queue takes: the commands and queries the application handles and the events it listens for, minus those a [receiver pool](#receiver-pools) takes. SNS hands a message to every subscription whose filter policy matches, so every application that listens for an event gets its own copy, and the instances of one application compete for it on their shared queue.

Replies bypass SNS entirely and are sent directly to the requesting instance's reply queue.

Startup creates what is missing and brings what exists up to the release that is starting, so it is safe to run on every start:

- **Queues.** SQS refuses to create a queue again with different attributes (`QueueNameExists`), so the transport looks such a queue up and sets its attributes (`VisibilityTimeout`, `RedrivePolicy`) on every start. A changed `VisibilityTimeoutSeconds` or `MaxReceiveCount` reaches the queues of a deployed system.
- **Subscriptions.** SNS refuses to subscribe a queue again with different attributes, which a release that adds or removes a handler would otherwise hit on every start. A new subscription is created with its filter policy and raw message delivery already set; an existing one is looked up, and the transport sets both on every start. SNS documents that a changed filter policy can take up to 15 minutes to take full effect.

While instances of two releases run side by side, the filter policy is the one the last instance to start set: a type the newer release added stops arriving if an instance of the older one restarts.

A `ProducerOnly` host creates only the topic and its reply queue: no application or pool queue, no subscription, and no poller but the reply queue's.

### Unicast by convention

SNS has no shared subscription that one application could hold for a command type, so a command or query reaches every application whose filter policy lists it. It reaches exactly one application only as long as exactly one application handles its type. Two applications that handle the same command each receive it, and both run their handler. For a request both reply: the first reply to arrive is the caller's answer, and the other arrives late and is deleted (`LateReply`). Handle each command or query type in one application.

### Filter policies and raw message delivery

SNS filter policies match on **message attributes**, and attributes are only visible to a filter policy when the subscription uses raw message delivery. Without it, SNS wraps the payload in a JSON envelope and the attributes move into the body, where filter policies cannot see them, so every queue would receive every message.

For that reason `RawMessageDelivery` defaults to `true` and the transport validates it at startup, throwing rather than starting up with silently-broken routing.

SNS accepts at most **150 values** in one filter policy, and each message type a queue takes is one value. The transport checks every queue's policy before it creates anything, and a queue that would take more than 150 types fails the start with an `InvalidOperationException` naming the queue, its application or pool, and the count. Move some of the types to a [receiver pool](#receiver-pools): each pool's queue has a subscription, and a filter policy, of its own.

### Name length

SQS queue names are capped at **80 characters**. An application or pool queue is named at most 76, so that its dead-letter queue, `{queue}-dlq`, fits too. `TopologyNaming` truncates and hashes names that would exceed the cap, so a long `SystemName`, application name or pool name will produce a hashed suffix rather than a failure. Names stay stable and collision-resistant, but they will not always be human-readable: keep `SystemName` and application names short if you want to recognise your queues in the AWS console.

Every resource name is also sanitized to `[A-Za-z0-9_-]` (the only characters SQS queue and SNS topic names accept), any other character becoming `-`. The default `ApplicationName` is the entry assembly's name, so an application named `Conduit.Sample.Orders` gets the queue `conduit-Conduit-Sample-Orders`, not `conduit-Conduit.Sample.Orders`. `AmazonSqsTopologyReaper.ReapByPrefixAsync` sanitizes the prefix it is given the same way, so a `john.doe` prefix reaps the `john-doe-…` resources.

### Auto-discovery

The transport reads `ConduitTypeRegistry`, which the source generator fills at compile time, to learn which types this application takes. There is no runtime scan of the container:

- An event type with an `IEventListener<T>` is added to a filter policy.
- A type with an `ICommandHandler<T>`, `ICommandHandler<T,R>` or `IQueryHandler<T,R>` is added to a filter policy.

The filter policy is that of the subscription of the queue that takes the type: the receiver pool's queue when a pool lists it, otherwise the application's queue. Each queue has one subscription (see [Topology](#topology)).

A listener registered at runtime counts too: `AddJobs` (an event trigger's event), `AddAuditListener`
(`AuditBatch`) and a `ConfigureConduitRegistrations` `HandleEvent` override activate their event in the
registry (`ConduitTypeRegistry.ActivateEvent`), so it is subscribed like an event whose listener the
generator found.

No manual subscription configuration is needed.

## Permissions

The identity the transport runs as needs these actions, each one a call the transport makes:

| Action | On | When |
|---|---|---|
| `sns:CreateTopic` | the topic | Every start: creates the topic, or returns the existing one |
| `sns:Subscribe` | the topic | Every start, for each application or pool queue that takes a type (never on a `ProducerOnly` host) |
| `sns:SetSubscriptionAttributes` | those subscriptions | Every start, after `Subscribe`: the filter policy and raw message delivery |
| `sns:Publish` | the topic | Every command, query and event sent |
| `sqs:CreateQueue` | the application's queues | Every start: the application, pool, dead-letter and reply queues |
| `sqs:GetQueueUrl` | the application's queues | A start that finds a queue with other attributes than it asks for (a changed setting) |
| `sqs:SetQueueAttributes` | the application's queues | Every start: visibility timeout, redrive policy, and the access policy that lets the topic send to the application and pool queues |
| `sqs:GetQueueAttributes` | the application's queues | Every start: the ARNs of the application, pool and dead-letter queues |
| `sqs:ReceiveMessage`, `sqs:DeleteMessage` | the application, pool and reply queues | Polling |
| `sqs:ChangeMessageVisibility` | the application and pool queues | Stop: handing received messages back at once (the batch call is authorized by this action) |
| `sqs:DeleteQueue` | the instance's reply queue | Stop |
| `sqs:SendMessage` | the reply queues of the applications that send it requests, and its own queue | A reply; a dead-letter replay through `IDeadLetters` |

A `ProducerOnly` host needs only `sns:CreateTopic`, `sns:Publish`, and, on its own reply queue, `sqs:CreateQueue`, `sqs:GetQueueUrl`, `sqs:SetQueueAttributes`, `sqs:ReceiveMessage`, `sqs:DeleteMessage` and `sqs:DeleteQueue`. [`ReapByPrefixAsync`](#developer-and-test-modes) needs `sqs:ListQueues`, `sqs:DeleteQueue`, `sns:ListTopics` and `sns:DeleteTopic`.

Without one of the start-time actions the host fails to start: `sns:SetSubscriptionAttributes` is needed on every start, and `sqs:GetQueueUrl` on the first start after a queue setting changed. The stop-time ones degrade instead: without `sqs:DeleteQueue` each stop logs `ReplyQueueNotDeleted` and leaves the instance's reply queue behind, and without `sqs:ChangeMessageVisibility` each stop logs `MessagesNotMadeVisible` and the messages it hands back reappear only when their visibility timeout lapses. The transport sets the access policy of each application and pool queue on every start, replacing any other policy on that queue.

Scope the grants by name prefix rather than by exact ARN: every reply queue carries a random instance id, so no policy can name it in advance.

- **SQS**, the application's own queues: `arn:aws:sqs:{region}:{account}:{prefix-}{systemName}-{appName}*` covers its queue, its pools' queues, their dead-letter queues and every instance's reply queue.
- **SQS**, replies it sends: `sqs:SendMessage` on `arn:aws:sqs:{region}:{account}:{prefix-}{systemName}-{requesterApp}-replies-*` for each application that sends it commands or queries.
- **SNS**: `arn:aws:sns:{region}:{account}:{prefix-}{systemName}*`, where the trailing wildcard also covers the topic's subscriptions (`{topicArn}:{id}`), which `sns:SetSubscriptionAttributes` acts on.

A [long name](#name-length) is cut and hashed after its first 67 characters (71 for a reply queue), so keep `{prefix-}{systemName}-{appName}` shorter than that for the prefix to match every queue.

## Developer and Test modes

`TopologyMode` on `ConduitOptions` controls resource naming:

| Mode | Prefix | Durable | Cleanup |
|---|---|---|---|
| `Production` | None, unless `TopologyPrefix` is set | Yes | None |
| `Developer` | `TopologyPrefix`, by default `Environment.UserName` | Yes | None: SNS and SQS have no auto-delete (see below) |
| `Test` | `TopologyPrefix`, by default `test-` and 8 random hex characters | Yes | None (see below) |

With the code below, the topic is `alice-conduit` instead of `conduit`; what the modes are for is in [Transports → Developer and Test modes](index.md#developer-and-test-modes).

SNS and SQS have no auto-delete, so ephemeral resources accumulate, dead-letter queues included, and so does the reply queue of an instance that ended without stopping (a crash, a kill). `AmazonSqsTopologyReaper.ReapByPrefixAsync(options, prefix)` deletes queues and topics whose names start with a prefix; deleting a topic removes its subscriptions with it. Conduit's own test suites call it once per test project, and it is available to applications for the same purpose.

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "alice";  // optional, defaults to Environment.UserName

    conduit.AddEndpoint("orders", endpoint =>
        endpoint.UseAmazonSqs(options => options.Region = RegionEndpoint.EUWest1));
});
```

## Request/response

When the generated mediator routes a command to an Amazon SQS endpoint:

1. The transport publishes the command to the SNS topic with `ReplyQueueAddress` and `CorrelationId` attributes.
2. The subscription filter policy routes it to the handling application's queue.
3. The handler runs and the reply is sent **directly to the sender's reply queue** with the matching correlation id, bypassing SNS.
4. The sender matches the reply by correlation id and deserializes the `DispatchResult`. A failed result throws `RemoteDispatchException` on the caller, carrying the remote error's code and, for an unexpected failure, its support reference.

Each transport instance has a reply queue of its own, `{systemName}-{appName}-replies-{instanceId}`, created when it starts and deleted when it stops, so the reply reaches the instance that sent the request however many instances of the application run. A reply that arrives after its request timed out or was cancelled is deleted and logged at Debug (`LateReply`, EventId 111). An instance that ends without stopping (a crash, a kill) leaves its reply queue behind, holding any reply that arrives for it until SQS's retention period (4 days) expires the reply. In Developer and Test mode [`ReapByPrefixAsync`](#developer-and-test-modes) deletes such queues with the rest of the prefix. Production has no reaper: an operator deletes leftover reply queues by hand (in the AWS console or with the CLI), one by one. Reaping the `-replies-` prefix would delete the reply queues of the instances still running too.

`VisibilityTimeoutSeconds` must be at least `ResponseTimeout`, or a request could become visible again, and be redelivered to another consumer, while the original caller is still waiting for its reply. The transport validates this at startup.

A request with no reply within `ResponseTimeout` (default 60 seconds) fails with a `TimeoutException` (`No reply to '{type}' from endpoint '{endpoint}' within {timeout}.`), the same on every broker transport. It is never an `OperationCanceledException`, which only the caller's own cancellation token produces, so a `catch (OperationCanceledException)` around a dispatch cannot swallow a remote that did not answer. A request still waiting when the transport stops fails with an `InvalidOperationException`.

## Failure handling

<a id="failed-messages"></a>

A broker transport settles each message once, by what became of its dispatch, and never retries it itself: retry happens inside the dispatch ([dispatch retry](../dispatch-retry.md), or a Polly pipeline from [Resilience](../resilience.md)). The rules every broker shares are in [Failed messages](index.md#failed-messages). On Amazon SQS:

- **A request whose handler throws** is answered: its caller receives the failure as its reply (`InternalError` with a support reference, or the refusal the handler threw), and the request is deleted from its queue. An unexpected failure is also captured when the host has Conduit's dead-letter store.
- **An event whose listener throws (or whose dispatch fails outside its listeners: a pipeline behavior throws, a listener cannot be resolved, a gate throws something other than a refusal), or a `[FireAndForget]` command whose handler fails unexpectedly**, has no caller to tell. Where it goes depends on whether the host registered Conduit's [dead-letter queue](../dead-letter-queue.md):
  - **With Conduit's dead-letter store** (`AddDurability(d => d.AddDeadLetterQueue())`), Conduit captures the failed message in its database, with the exception and the message context, and deletes it from SQS. The message is browsed and replayed through `IDeadLetters`; a replay is sent straight to the application's queue.
  - **Without one**, the transport leaves the message invisible until its visibility timeout lapses. SQS then delivers it again, and once it has delivered it `MaxReceiveCount` times (5 by default) the queue's redrive policy moves it to the queue's dead-letter queue, `{queue}-dlq`, with its body and message attributes intact. So a listener that always fails runs `MaxReceiveCount` times, `VisibilityTimeoutSeconds` apart, then stops. The dead-letter queue keeps a message for 14 days, the longest SQS allows, counted from when the message was first sent; inspect it, and move it back once the cause is fixed with the SQS console's dead-letter queue redrive.
- **A message this host cannot read, or one it refuses** (an undispatchable body, a refused event or fire-and-forget command) is deleted, and captured first when it is unreadable and the host has the store, as on every broker.

SQS counts every delivery toward `MaxReceiveCount`, including one the transport handed back undispatched while [stopping](#stopping). A failure outside the dispatch itself (deleting the message failed, say) is logged as an Error (`InboundMessageFailed`, EventId 110) and leaves the message to be delivered again once its visibility timeout lapses; the poller carries on with the next message. The reply queue and the dead-letter queues have no redrive policy of their own.

### Stopping

When the host stops, the transport follows the [shared stop sequence](index.md#stopping-a-host-with-work-in-flight). It stops polling the application and pool queues; a message it had received but not started, and one handed over after the drain began, is made visible again at once (`ChangeMessageVisibility` to 0) for another instance to take, rather than when its visibility timeout lapses. The reply queue is polled through the drain, since a handler being drained may await a reply, and is then deleted, within the host's shutdown deadline or, failing that, when the host disposes the transport; a deletion that still fails is logged as a Warning (`ReplyQueueNotDeleted`, EventId 112) and leaves the queue behind, as a crash does (see [Request/response](#requestresponse)). Handing messages back is best effort too: what SQS does not take back at once is logged as a Warning with the count (`MessagesNotMadeVisible`, EventId 113) and reappears when its visibility timeout lapses.

## Wire format

Messages use JSON serialization (`ConduitRuntime.JsonOptions`) with metadata in SNS/SQS **message attributes**:

| Attribute | Value |
|---|---|
| Body | JSON-serialized message payload |
| `Type` | Full type name (e.g. `"Orders.Commands.CreateOrder"`) |
| `MessageId` | From `MessageContext` or auto-generated |
| `CausationId` | From `MessageContext.CausationId` |
| `ConversationId` | From `MessageContext.ConversationId` |
| `ReplyQueueAddress` | Reply queue URL (requests only) |
| `CorrelationId` | Request/response correlation (requests and their replies only) |
| `traceparent`, `tracestate` | W3C trace context, when the publish runs inside a trace |
| `conduit-source-app` | The publishing application's `ApplicationName`, used by the [self-published event filter](index.md#self-published-events) (omitted when `ApplicationName` is unset) |

The three correlation identifiers use short names rather than the canonical `conduit-message-id` style the other transports carry. That is a hard constraint, not a style choice: **SNS and SQS cap a message at 10 attributes**, and a request publish already uses 9 of them. Carrying only the short names keeps the message under the ceiling where both forms would not, leaving exactly one attribute spare.

Keep that ceiling in mind if you were hoping to add your own attributes to Conduit messages: there is almost no room.

## Receiver pools

For concurrency isolation, configure named receiver pools:

```csharp
endpoint.UseAmazonSqs(options =>
{
    options.Region = RegionEndpoint.EUWest1;
    options.DefaultConcurrency = 20;

    options.AddPool("imports", pool =>
    {
        pool.Concurrency = 2;
        pool.Handle("Catalog.Commands.BulkImportCommand");
    });
});
```

This routes `BulkImportCommand` to a dedicated `{systemName}-{appName}-imports` queue with its own SNS subscription, filter policy and dead-letter queue, where at most 2 handlers run at once. Everything else uses the default queue at 20. A slow pool handler cannot starve the default queue's handlers. The pool's queue takes all of the pool's types, commands, queries and events alike, and a pool's `Concurrency` defaults to `1`.

A pool can be declared in configuration (the endpoint's `Pools` array) and in code under the same name: the two merge into one pool (see [Pool merging](../configuration-reference.md#pool-merging)). The names `replies` and `dead-letter` are reserved.

Remember the [80-character queue-name cap](#name-length) when naming pools: a long pool name on a long application name will be hashed. A pool whose queue would take the name of another of the application's dead-letter queues (a pool named `dlq` would take the application queue's) fails the start with an `InvalidOperationException`.

## Limitations

- **Unicast by convention.** Two applications that handle the same command or query type both receive it ([Unicast by convention](#unicast-by-convention)).
- **No auto-delete.** Developer and Test resources, and the reply queues of crashed instances, stay until someone deletes them ([Developer and Test modes](#developer-and-test-modes)).
- **No visibility extension.** The transport does not extend a message's visibility while its handler runs. A handler that outlasts `VisibilityTimeoutSeconds` sees its message delivered again, to it or another instance, while it is still running; and a message received in a batch but waiting for a free handler slot spends its visibility timeout waiting.
- **Standard queues.** SQS standard queues keep no order and may deliver a message more than once even without a failure; use the [inbox](../inbox.md) where a duplicate matters.
- **One attribute spare.** A message carries at most 10 attributes, and Conduit uses 9 ([Wire format](#wire-format)).
- **150 types per queue.** A filter policy takes at most 150 values ([Filter policies and raw message delivery](#filter-policies-and-raw-message-delivery)).
- **Filter changes are eventual.** A changed filter policy can take up to 15 minutes to take full effect, and the last instance to start sets it.

---

Previous: [Azure Service Bus](azure-service-bus.md) | [Home](../../README.md) | Next: [ActiveMQ](activemq.md)
