# Observability

- [Overview](#overview)
- [Setup](#setup)
- [Traces](#traces)
  - [Spans](#spans)
  - [Tags](#tags)
  - [Status](#status)
  - [Trace context across services](#trace-context-across-services)
- [Metrics](#metrics)
  - [Dispatch](#dispatch)
  - [The `(undeclared)` type](#the-undeclared-type)
  - [Transports](#transports)
  - [Retries and durability](#retries-and-durability)
  - [Polly](#polly)
- [The audit trail](#the-audit-trail)
- [Limitations](#limitations)

## Overview

Conduit instruments itself with .NET's own diagnostics APIs. The instrumentation is in the core package (`Conduit.NET`), so there is no package to add and no behavior to register:

| Instrumentation | Name |
|---|---|
| `ActivitySource` (traces) | `Conduit` |
| `Meter` (metrics) | `Conduit` |

When nothing listens to them, no span is created and no measurement is kept. Any OpenTelemetry exporter reads them once the host subscribes to the source and the meter.

Traces and metrics show volume, latency, failure rates and the path a request takes across services. What happened to one particular message is the [audit trail](#the-audit-trail)'s question.

## Setup

With the OpenTelemetry SDK (`OpenTelemetry.Extensions.Hosting`), subscribe to both names:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Conduit"))
    .WithMetrics(metrics => metrics.AddMeter("Conduit"));
```

Add an exporter to each (OTLP, Prometheus, ...) as for any other source. Conduit traces no HTTP request itself: ASP.NET Core's and `HttpClient`'s instrumentation (`AddAspNetCoreInstrumentation`, `AddHttpClientInstrumentation`, from their own OpenTelemetry packages) covers the requests that carry its HTTP and SignalR traffic.

## Traces

### Spans

| Span | Kind | Started by |
|---|---|---|
| `ConduitMediator/DispatchAsync` | Internal | Every in-process `ExecuteAsync`, `FetchAsync` and `PublishAsync`, a dispatch from inside a handler and a REST endpoint's (`MapConduitPost`, ...) included. A routed command or query's span covers the send and the wait for the reply. |
| `ConduitRuntime/HandleWireRequest` | Internal | A command or query arriving over the wire: the HTTP envelope endpoint, the SignalR hub, a broker transport. |
| `ConduitRuntime/HandleWireEvent` | Internal | An event arriving over the wire. |
| `ConduitRuntime/PreflightWireRequestAsync` | Internal | A [wire preflight](pipeline-behaviors.md#inbound-checks-and-the-wire-preflight). |
| ``BrokerTransportBase`1/SendRequestAsync`` | Producer | A broker transport sending a request; the span covers the wait for the reply. |
| ``BrokerTransportBase`1/SendEventAsync`` | Producer | A broker transport sending an event, or a routed `[FireAndForget]` command. |
| ``BrokerTransportBase`1/DispatchInboundMessageAsync`` | Consumer | A broker transport receiving a message, as a child of the sender's producer span. Its wire span is its child. |

The broker spans are named after the transports' shared base class, ``BrokerTransportBase`1``. The `conduit.transport` tag names the transport.

### Tags

| Tag | On | Value |
|---|---|---|
| `conduit.message.type` | Every span | The message's full type name. A wire span carries the name as the caller sent it, an unknown one included. |
| `conduit.message.kind` | Mediator and wire spans | `command`, `query` or `event` on a mediator span; `request` (a command or a query) or `event` on a wire span. |
| `conduit.message.id` | Spans started inside a dispatch | The dispatch's `MessageContext.MessageId`. |
| `conduit.conversation.id` | Spans started inside a dispatch | The dispatch's `MessageContext.ConversationId`. |
| `conduit.dispatch` | A command's or a query's mediator span, once it is handed to its handler or sent to its endpoint | `local` (handled on this host) or `remote` (sent to an endpoint). Absent when the dispatch throws for want of a route or a handler, and on a routed `[FireAndForget]` command the outbox captures. |
| `conduit.endpoint` | A command's or a query's mediator span, once it is sent to its endpoint | The endpoint's name. |
| `conduit.transport` | Broker spans | The transport's class name: `RabbitMQTransport`, `AzureServiceBusTransport`, `AmazonSqsTransport` or `ActiveMQTransport`. |

The ids are on every mediator span and on the producer spans. A wire span carries them only when its edge started the message's context before the span: a broker transport always does; the HTTP endpoint and the hub do only when they honor the caller's ids ([Security → Correlation ids](security.md#correlation-ids)) or have properties to carry (client properties, or an enricher's: [Security → Client properties](security.md#client-properties)). Otherwise the context is minted after the wire span has started, and that span has no ids; the spans of the dispatches its handler makes carry the minted `ConversationId`. A preflight runs under no context, so its span has none. A consumer span starts before the message's context is read, so it has none either; its wire span carries them.

### Status

Only the wire spans set a status. It is `Ok` when the request or event was handled, or discarded by a gate, and `Error` otherwise, described by the exception's message: a refusal's message, or for an unexpected failure the real exception's message, which the caller never sees. The mediator and broker spans leave the status unset; a failed in-process dispatch shows in the dispatch metric's `conduit.outcome`.

### Trace context across services

A broker transport writes the producer span's trace context into the message's native properties through .NET's `DistributedContextPropagator.Current` (W3C `traceparent` and `tracestate` by default), and the receiving transport reads it back. Its consumer span is the producer span's child, so a command routed through a broker to another service and handled there is one trace. The context is written only when the producer span exists, that is, when the sending host listens to the `Conduit` source.

Conduit's HTTP and SignalR transports start no spans and write no trace headers of their own. A wire request they bring in gets its `ConduitRuntime/HandleWireRequest` span under whatever span is current, ASP.NET Core's request span when its instrumentation is on; whether the caller's trace continues into it is up to ASP.NET Core, SignalR and `HttpClient`.

## Metrics

All on the `Conduit` meter:

| Metric | Instrument | Unit | Tags |
|---|---|---|---|
| `conduit.dispatch.count` | `Counter<long>` | | `conduit.message.type`, `conduit.message.kind`, `conduit.outcome` |
| `conduit.dispatch.duration` | `Histogram<double>` | `ms` | `conduit.message.type`, `conduit.message.kind`, `conduit.outcome` |
| `conduit.transport.send.count` | `Counter<long>` | | `conduit.message.type`, `conduit.transport` |
| `conduit.transport.send.duration` | `Histogram<double>` | `ms` | `conduit.message.type`, `conduit.transport` |
| `conduit.transport.receive.count` | `Counter<long>` | | `conduit.message.type`, `conduit.transport`, `conduit.outcome` |
| `conduit.dispatch.retries` | `Counter<long>` | | `conduit.message.type`, `conduit.retry.reason`, `conduit.retry.outcome` |
| `conduit.messages.dead_lettered` | `Counter<long>` | | `conduit.message.type`, `conduit.source.endpoint` |
| `conduit.messages.replayed` | `Counter<long>` | | `conduit.message.type` |
| `conduit.outbox.retries_exhausted` | `Counter<long>` | | `conduit.message.type` |

### Dispatch

`conduit.dispatch.count` and `conduit.dispatch.duration` measure every dispatch the mediator runs, once: an in-process `ExecuteAsync`, `FetchAsync` or `PublishAsync`, a wire request or event arriving over any transport, and an outbox's delivery to this host's listeners. A dispatch from inside a handler is measured as its own dispatch. A request routed to another service is measured on both hosts: on the sender, the send and the wait for the reply; on the receiver, as a wire request.

| Tag | Value |
|---|---|
| `conduit.message.type` | The message's full type name, or [`(undeclared)`](#the-undeclared-type). |
| `conduit.message.kind` | `command`, `query` or `event`, or `request` for an undeclared wire request. A wire request takes its type's declared kind, so an in-process command and the same command over the wire share a series. |
| `conduit.outcome` | `error` when the dispatch threw, a refusal included (a `NotFoundException`, a failed `Validate()`); `success` otherwise. |

A wire request or event that an inbound gate refuses or discards never reaches the mediator, so it is not measured here. Its wire span still records it.

### The `(undeclared)` type

A wire request or event names its type with a string the caller chooses. So the dispatch metric's type tag comes from the host's own declarations: a type the host declares is tagged with its full name, and any other name with `conduit.message.type` = `(undeclared)`, kind `request` for a request and `event` for an event. A caller cannot mint metric series by sending made-up names.

"Declares" means the source generator discovered the type in the host's compilation, referenced assemblies included: a command or query for a request, an event for an event. Two kinds of message are tagged `(undeclared)`:

- a request this host forwards by route to another endpoint without declaring its type;
- a name nothing on this host declares, which is refused with `BadRequest` ([Error Handling](error-handling.md#unhandled-exceptions)) and measured with `conduit.outcome` = `error`.

Only the dispatch metrics are bounded this way. A wire span's `conduit.message.type`, and the transport metrics below, carry the name as it arrived.

### Transports

The broker transports (RabbitMQ, Azure Service Bus, Amazon SQS, ActiveMQ) record the transport metrics. The HTTP and SignalR transports record none.

- `conduit.transport.send.count` and `conduit.transport.send.duration` measure each send that succeeds: a request (with the wait for its reply) or an event. A failed send is not measured.
- `conduit.transport.receive.count` counts each typed message received; a message with no type is acknowledged before any span or measurement. Its `conduit.outcome` is `error` only when the message could not be turned into a dispatch (its context or its body could not be read) or the transport failed while handling it. A handler's failure is still a `success` here; the dispatch metrics count it.
- `conduit.transport` is the transport's class name, as on the spans.

### Retries and durability

| Metric | Counts |
|---|---|
| `conduit.dispatch.retries` | Each failed attempt the [dispatch retry](dispatch-retry.md#observing-retries) policy gave a reason for: `conduit.retry.reason` is that reason, `conduit.retry.outcome` is `retried`, or `exhausted` on the last attempt. Polly's retries are not counted here. |
| `conduit.messages.dead_lettered` | Each message a broker transport captured to the [dead-letter queue](dead-letter-queue.md). `conduit.source.endpoint` is the endpoint the transport serves. |
| `conduit.messages.replayed` | Each dead-letter entry a replay redelivered. |
| `conduit.outbox.retries_exhausted` | Each outbox entry marked Failed after exhausting its retries. The relay records an untagged zero when it starts, so the series exists before the first failure ([Outbox → Alerting on exhausted retries](outbox.md#alerting-on-exhausted-retries)). |

### Polly

`Conduit.NET.Resilience` adds no metric of its own. Polly reports its pipelines' events and durations on its own meter, `Polly`: add `.AddMeter("Polly")` to the metrics builder to export them. Each Polly retry inside a dispatch is recorded on that dispatch's audit record instead ([Resilience → Observing retries](resilience.md#observing-retries)).

## The audit trail

The [audit trail](audit.md) (`Conduit.NET.Audit`) is the other observability surface. It keeps one record per dispatch in a database: the message's ids (`MessageId`, `ConversationId`, `CausationId`), its payload (redacted as you mark it), and every observation the dispatch made, from handler start and end to each retry, each transport send, a dead-lettering, and the outcome. It answers what happened to one message, and aggregates nothing; the meter and the source answer how many, how fast, and by which path.

The two share their ids. A span's `conduit.message.id` and `conduit.conversation.id` are the audit record's `MessageId` and `ConversationId`, so a trace leads to the records of the dispatches in it whose spans carry them: every in-process and nested dispatch, and a wire dispatch arriving by broker, or over the HTTP endpoint or the hub when that edge honors the caller's ids or carries properties. A default HTTP or hub wire span has no ids ([Tags](#tags)); the `ConversationId` on its nested dispatches' spans still leads to its record.

## Limitations

- **In-process and broker spans carry no status.** A failed `ExecuteAsync` shows as a span with its status unset; read failures from `conduit.outcome`, or from the wire span on the receiving host.
- **The HTTP and SignalR transports have no spans or transport metrics of their own.** The wire spans and the dispatch metrics still cover what they bring in.
- **Some deliveries start no span.** The outbox's delivery to this host's listeners is measured but has no span, and a dead-letter replay's redelivery starts no producer span.
- **A default HTTP or hub wire span has no message ids.** Unless the edge honors the caller's ids or has properties to carry, the context is minted after the span starts ([Tags](#tags)).
- **The transport metrics carry the type name as it arrived.** `conduit.transport.receive.count` and `conduit.messages.dead_lettered` are tagged with whatever type a producer on the broker set. A request this host forwards by route without declaring its type is sent under the caller's name too, so on a broker transport that name tags `conduit.transport.send.*` and the producer span. Only the dispatch metrics bound it with [`(undeclared)`](#the-undeclared-type).

---

Previous: [Resilience](resilience.md) | [Home](../README.md) | Next: [Dispatch Retry](dispatch-retry.md)
