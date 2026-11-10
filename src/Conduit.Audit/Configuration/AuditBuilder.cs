using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Fluent builder for configuring the audit capture side (the observer that emits
/// <see cref="Runtime.AuditBatch"/> events). Exposes batching/channel tuning and per-type
/// suppression. To persist audit records, pair this with <c>AddAuditListener</c>.
/// </summary>
public sealed class AuditBuilder
{
    internal IServiceCollection Services { get; }
    internal AuditOptions Options { get; } = new();
    internal HashSet<Type> SuppressedTypes { get; } = [];
    internal HashSet<Type> SuppressedPayloadTypes { get; } = [];

    internal AuditBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>The service name stamped on emitted batches. Defaults to the host application name, or "unknown".</summary>
    public string? ServiceName
    {
        get => this.Options.ServiceName;
        set => this.Options.ServiceName = value;
    }

    /// <summary>Maximum number of records per batch before publishing. Positive. Default 100.</summary>
    public int BatchSize
    {
        get => this.Options.BatchSize;
        set => this.Options.BatchSize = value;
    }

    /// <summary>Maximum number of records buffered before backpressure applies. Positive. Default 10,000.</summary>
    public int ChannelCapacity
    {
        get => this.Options.ChannelCapacity;
        set => this.Options.ChannelCapacity = value;
    }

    /// <summary>Behavior when the buffer is full. Default <see cref="BoundedChannelFullMode.DropOldest"/>.</summary>
    public BoundedChannelFullMode BackpressureMode
    {
        get => this.Options.BackpressureMode;
        set => this.Options.BackpressureMode = value;
    }

    /// <summary>
    /// How long a dispatch may go without an observation before it gets a provisional record, with no outcome; its real
    /// record follows when it ends. Positive, and at most one day. Default 5 minutes.
    /// </summary>
    public TimeSpan StaleGroupTimeout
    {
        get => this.Options.StaleGroupTimeout;
        set => this.Options.StaleGroupTimeout = value;
    }

    /// <summary>
    /// How long the batch publisher keeps draining queued records when the host stops, at most (the host's shutdown
    /// deadline ends it sooner); what is left then is counted in one Warning. Not negative, and at most one day. Default
    /// 5 seconds.
    /// </summary>
    public TimeSpan ShutdownGrace
    {
        get => this.Options.ShutdownGrace;
        set => this.Options.ShutdownGrace = value;
    }

    /// <summary>
    /// The largest request body, in UTF-8 bytes, a sealed record keeps. Default 64 KB; 0 or less means no cap; an enabled
    /// cap below 1 KB is refused by <c>AddAudit</c>. A body over the cap is stored with its longest strings shortened and a
    /// <c>$truncated</c> marker, or as the marker alone when that cannot fit (see <see cref="AuditOptions.MaxPayloadBytes"/>).
    /// </summary>
    public int MaxPayloadBytes
    {
        get => this.Options.MaxPayloadBytes;
        set => this.Options.MaxPayloadBytes = value;
    }

    /// <summary>
    /// Suppresses all audit recording for the specified message type.
    /// No observations will be captured or published for dispatches of <typeparamref name="T"/>.
    /// </summary>
    public AuditBuilder Suppress<T>()
    {
        this.SuppressedTypes.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Suppresses payload capture for the specified message type.
    /// Observations are still recorded, but the message body is not included in audit records.
    /// Use for types containing sensitive data.
    /// </summary>
    public AuditBuilder SuppressPayload<T>()
    {
        this.SuppressedPayloadTypes.Add(typeof(T));
        return this;
    }
}
