using System.Threading.Channels;
using Conduit.Audit.Configuration;
using Conduit.Audit.Runtime;
using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Audit;

/// <summary>
/// Extension methods for registering the audit trail on <see cref="ConduitBuilder"/>.
/// </summary>
public static class ConduitBuilderExtensions
{
    /// <summary>
    /// Adds the audit trail subsystem to the Conduit configuration.
    /// Registers the observer, batch publisher, and channel infrastructure
    /// needed to capture and publish dispatch audit records.
    /// </summary>
    /// <param name="builder">The Conduit builder to configure.</param>
    /// <param name="configure">Optional callback to configure audit options and per-type suppression.</param>
    /// <returns>The original <paramref name="builder"/> for chaining.</returns>
    public static ConduitBuilder AddAudit(
        this ConduitBuilder builder,
        Action<AuditBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var auditBuilder = new AuditBuilder(builder.Services);
        configure?.Invoke(auditBuilder);

        // Configure options via IOptions<AuditOptions>
        builder.Services.Configure<AuditOptions>(options =>
        {
            options.BatchSize = auditBuilder.Options.BatchSize;
            options.FlushInterval = auditBuilder.Options.FlushInterval;
            options.ServiceName = auditBuilder.Options.ServiceName;
            options.StaleGroupTimeout = auditBuilder.Options.StaleGroupTimeout;
            options.ChannelCapacity = auditBuilder.Options.ChannelCapacity;
            options.BackpressureMode = auditBuilder.Options.BackpressureMode;
            options.MetricsAggregationInterval = auditBuilder.Options.MetricsAggregationInterval;
        });

        // Channel for observation consolidation -> batch publishing
        var channel = Channel.CreateBounded<DispatchAuditRecord>(
            new BoundedChannelOptions(auditBuilder.Options.ChannelCapacity)
            {
                FullMode = auditBuilder.Options.BackpressureMode,
                SingleReader = true,
                SingleWriter = false,
            });
        builder.Services.AddSingleton(channel.Writer);
        builder.Services.AddSingleton(channel.Reader);

        // Observer (consolidates observations per dispatch)
        builder.Services.AddSingleton(serviceProvider =>
        {
            var serviceName = auditBuilder.Options.ServiceName
                ?? serviceProvider.GetService<IHostEnvironment>()?.ApplicationName
                ?? "unknown";
            return new AuditObserver(channel.Writer, serviceName);
        });

        // Wire observer at startup, unwire at shutdown
        builder.Services.AddHostedService<AuditObserverInitializer>();

        // Background publisher (drains channel, publishes AuditBatch events)
        builder.Services.AddHostedService<AuditBatchPublisher>();

        return builder;
    }

    /// <summary>
    /// Adds an audit listener to the Conduit configuration. The listener consumes
    /// <see cref="AuditBatch"/> events and persists them, owning the store, table initializer,
    /// and retention purge. The events may be published in this process (pair with
    /// <see cref="AddAudit"/>) or arrive over a transport from other services (a central collector);
    /// either way the listener handles them identically.
    /// </summary>
    /// <param name="builder">The Conduit builder to configure.</param>
    /// <param name="configure">Callback to configure the listener's database and retention.</param>
    /// <returns>The original <paramref name="builder"/> for chaining.</returns>
    public static ConduitBuilder AddAuditListener(
        this ConduitBuilder builder,
        Action<AuditListenerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var listenerBuilder = new AuditListenerBuilder(builder.Services);
        configure(listenerBuilder);

        // Retention/purge are owned by the listener (it registers the purge service).
        builder.Services.Configure<AuditOptions>(options =>
        {
            options.RetentionPeriod = listenerBuilder.Options.RetentionPeriod;
            options.PurgeInterval = listenerBuilder.Options.PurgeInterval;
        });

        return builder;
    }
}
