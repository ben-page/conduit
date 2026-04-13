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
            options.RetentionPeriod = auditBuilder.Options.RetentionPeriod;
            options.PurgeInterval = auditBuilder.Options.PurgeInterval;
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
}
