using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Conduit.Runtime;

/// <summary>
/// Centralized OpenTelemetry instrumentation for Conduit.
/// Owns the ActivitySource and Meter. Zero cost when no listener is attached.
/// Not intended for direct use — called by generated code and runtime infrastructure.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ConduitTelemetry
{
    internal static readonly ActivitySource Source = new("Conduit");
    internal static readonly Meter MeterInstance = new("Conduit");

    private static readonly Counter<long> DispatchCount =
        MeterInstance.CreateCounter<long>("conduit.dispatch.count", description: "Number of dispatched messages");

    private static readonly Histogram<double> DispatchDuration =
        MeterInstance.CreateHistogram<double>("conduit.dispatch.duration", unit: "ms", description: "Dispatch duration");

    private static readonly Counter<long> TransportSendCount =
        MeterInstance.CreateCounter<long>("conduit.transport.send.count", description: "Number of transport sends");

    private static readonly Histogram<double> TransportSendDuration =
        MeterInstance.CreateHistogram<double>("conduit.transport.send.duration", unit: "ms", description: "Transport send duration");

    private static readonly Counter<long> TransportReceiveCount =
        MeterInstance.CreateCounter<long>("conduit.transport.receive.count", description: "Number of transport receives");

    private static readonly Counter<long> DeadLetteredCount =
        MeterInstance.CreateCounter<long>("conduit.messages.dead_lettered", description: "Number of messages captured to the dead letter queue");

    private static readonly Counter<long> OutboxRetriesExhaustedCount =
        MeterInstance.CreateCounter<long>("conduit.outbox.retries_exhausted", description: "Number of outbox entries marked Failed after exhausting their retries");

    private static readonly Counter<long> ReplayedCount =
        MeterInstance.CreateCounter<long>("conduit.messages.replayed", description: "Number of messages replayed from the dead letter queue");

    private static readonly Counter<long> DispatchRetryCount =
        MeterInstance.CreateCounter<long>("conduit.dispatch.retries", description: "Number of failed dispatch attempts the retry policy gave a retry reason for, retried or exhausted");

    /// <summary>
    /// Starts an activity for internal operations (local dispatch, wire dispatch).
    /// Returns null when no listener is attached (zero-cost).
    /// </summary>
    public static Activity? StartActivity<TCaller>(
        string messageType,
        string messageKind,
        [CallerMemberName] string operation = "")
        => StartActivity(typeof(TCaller).Name, messageType, messageKind, operation);

    /// <summary>
    /// Starts an activity for internal operations using an explicit caller name.
    /// Use this overload when the caller is a static class (which cannot be a type argument).
    /// Returns null when no listener is attached (zero-cost).
    /// </summary>
    public static Activity? StartActivity(
        string callerName,
        string messageType,
        string messageKind,
        string operation)
    {
        var activity = Source.StartActivity(
            $"{callerName}/{operation}",
            ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag("conduit.message.type", messageType);
            activity.SetTag("conduit.message.kind", messageKind);
            SetMessageContextTags(activity);
        }

        return activity;
    }

    /// <summary>
    /// Starts a producer activity for outbound transport sends.
    /// </summary>
    public static Activity? StartProducerActivity<TCaller>(
        string messageType,
        [CallerMemberName] string operation = "")
    {
        var activity = Source.StartActivity(
            $"{typeof(TCaller).Name}/{operation}",
            ActivityKind.Producer);

        if (activity is not null)
        {
            activity.SetTag("conduit.message.type", messageType);
            SetMessageContextTags(activity);
        }

        return activity;
    }

    /// <summary>
    /// Starts a consumer activity for inbound transport receives.
    /// Links to the parent context extracted from message headers.
    /// </summary>
    public static Activity? StartConsumerActivity<TCaller>(
        string messageType,
        ActivityContext? parentContext,
        [CallerMemberName] string operation = "")
    {
        var activity = parentContext.HasValue
            ? Source.StartActivity(
                $"{typeof(TCaller).Name}/{operation}",
                ActivityKind.Consumer,
                parentContext: parentContext.Value)
            : Source.StartActivity(
                $"{typeof(TCaller).Name}/{operation}",
                ActivityKind.Consumer);

        if (activity is not null)
        {
            activity.SetTag("conduit.message.type", messageType);
            SetMessageContextTags(activity);
        }

        return activity;
    }

    /// <summary>
    /// Injects W3C trace context (traceparent/tracestate) into a carrier
    /// using the current <see cref="DistributedContextPropagator"/>.
    /// Each transport maps the setter to its native header format.
    /// </summary>
    public static void InjectTraceContext(Activity? activity, Action<string, string> setter)
    {
        if (activity is null)
        {
            return;
        }

        DistributedContextPropagator.Current.Inject(
            activity, setter, static (carrier, key, value) =>
            {
                if (!string.IsNullOrEmpty(value))
                {
                    ((Action<string, string>)carrier!)(key, value);
                }
            });
    }

    /// <summary>
    /// Extracts W3C trace context from a carrier, returning the parent ActivityContext
    /// for linking consumer spans to producer spans across transport boundaries.
    /// </summary>
    public static ActivityContext? ExtractTraceContext(Func<string, string?> getter)
    {
        DistributedContextPropagator.Current.ExtractTraceIdAndState(
            getter,
            static (carrier, key, out value, out values) =>
            {
                value = ((Func<string, string?>)carrier!)(key);
                values = null;
            },
            out var traceParent,
            out var traceState);

        if (string.IsNullOrEmpty(traceParent))
        {
            return null;
        }

        return ActivityContext.TryParse(traceParent, traceState, out var context)
            ? context
            : null;
    }

    /// <summary>
    /// Records dispatch count and duration metrics.
    /// </summary>
    public static void RecordDispatch(string messageType, string messageKind, bool success, double durationMs)
    {
        var outcome = success ? "success" : "error";

        DispatchCount.Add(1,
            new KeyValuePair<string, object?>("conduit.message.type", messageType),
            new KeyValuePair<string, object?>("conduit.message.kind", messageKind),
            new KeyValuePair<string, object?>("conduit.outcome", outcome));

        DispatchDuration.Record(durationMs,
            new KeyValuePair<string, object?>("conduit.message.type", messageType),
            new KeyValuePair<string, object?>("conduit.message.kind", messageKind),
            new KeyValuePair<string, object?>("conduit.outcome", outcome));
    }

    /// <summary>
    /// Records transport send count and duration metrics.
    /// </summary>
    public static void RecordTransportSend(string messageType, string transport, double durationMs)
    {
        TransportSendCount.Add(1,
            new KeyValuePair<string, object?>("conduit.message.type", messageType),
            new KeyValuePair<string, object?>("conduit.transport", transport));

        TransportSendDuration.Record(durationMs,
            new KeyValuePair<string, object?>("conduit.message.type", messageType),
            new KeyValuePair<string, object?>("conduit.transport", transport));
    }

    /// <summary>
    /// Records transport receive count metric.
    /// </summary>
    public static void RecordTransportReceive(string messageType, string transport, bool success)
    {
        TransportReceiveCount.Add(1,
            new KeyValuePair<string, object?>("conduit.message.type", messageType),
            new KeyValuePair<string, object?>("conduit.transport", transport),
            new KeyValuePair<string, object?>("conduit.outcome", success ? "success" : "error"));
    }

    /// <summary>
    /// Records a dead letter capture metric.
    /// </summary>
    public static void RecordDeadLettered(string messageType, string sourceEndpoint)
    {
        DeadLetteredCount.Add(1,
            new KeyValuePair<string, object?>("conduit.message.type", messageType),
            new KeyValuePair<string, object?>("conduit.source.endpoint", sourceEndpoint));
    }

    /// <summary>
    /// Records a zero on the outbox-exhaustion counter, so its series exists before the first exhaustion. An
    /// OpenTelemetry counter exports nothing until it is first recorded, so without this an alert on
    /// <c>conduit.outbox.retries_exhausted</c> watches a series that does not exist, and the only proof the export
    /// works would be a real failure. The zero carries no <c>conduit.message.type</c>: no type has failed.
    /// </summary>
    public static void InitializeOutboxRetriesExhausted()
    {
        OutboxRetriesExhaustedCount.Add(0);
    }

    /// <summary>
    /// Records an outbox entry marked Failed after exhausting its retries.
    /// </summary>
    public static void RecordOutboxRetriesExhausted(string messageType)
    {
        OutboxRetriesExhaustedCount.Add(1, new KeyValuePair<string, object?>("conduit.message.type", messageType));
    }

    /// <summary>
    /// Records a dead letter replay metric.
    /// </summary>
    public static void RecordReplayed(string messageType)
    {
        ReplayedCount.Add(1,
            new KeyValuePair<string, object?>("conduit.message.type", messageType));
    }

    /// <summary>
    /// Records a failed dispatch attempt the retry policy gave <paramref name="reason"/> for: <c>retried</c>, or
    /// <c>exhausted</c> when no attempts were left.
    /// </summary>
    internal static void RecordDispatchRetry(string messageType, string reason, bool exhausted)
    {
        DispatchRetryCount.Add(1,
            new KeyValuePair<string, object?>("conduit.message.type", messageType),
            new KeyValuePair<string, object?>("conduit.retry.reason", reason),
            new KeyValuePair<string, object?>("conduit.retry.outcome", exhausted ? "exhausted" : "retried"));
    }

    private static void SetMessageContextTags(Activity activity)
    {
        var context = MessageContext.Current;
        if (context is not null)
        {
            activity.SetTag("conduit.message.id", context.MessageId);
            activity.SetTag("conduit.conversation.id", context.ConversationId);
        }
    }
}
