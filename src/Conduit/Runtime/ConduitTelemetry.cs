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

    /// <summary>
    /// Starts an activity for internal operations (local dispatch, wire dispatch).
    /// Returns null when no listener is attached (zero-cost).
    /// </summary>
    public static Activity? StartActivity<TCaller>(
        string messageType,
        string messageKind,
        [CallerMemberName] string operation = "")
    {
        var activity = Source.StartActivity(
            $"{typeof(TCaller).Name}/{operation}",
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
