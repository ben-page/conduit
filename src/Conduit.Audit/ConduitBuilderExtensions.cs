using System.Threading.Channels;
using Conduit.Audit.Configuration;
using Conduit.Audit.Runtime;
using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">An option the callback set is invalid
    /// (<see cref="AuditOptions.Validate"/>); each failure names it.</exception>
    public static ConduitBuilder AddAudit(
        this ConduitBuilder builder,
        Action<AuditBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var auditBuilder = new AuditBuilder(builder.Services);
        configure?.Invoke(auditBuilder);
        auditBuilder.Options.Validate();
        ValidateBoundOptionsOnStart(builder.Services);

        // Configure options via IOptions<AuditOptions>
        builder.Services.Configure<AuditOptions>(options =>
        {
            options.BatchSize = auditBuilder.Options.BatchSize;
            options.ServiceName = auditBuilder.Options.ServiceName;
            options.StaleGroupTimeout = auditBuilder.Options.StaleGroupTimeout;
            options.ShutdownGrace = auditBuilder.Options.ShutdownGrace;
            options.ChannelCapacity = auditBuilder.Options.ChannelCapacity;
            options.BackpressureMode = auditBuilder.Options.BackpressureMode;
            options.MaxPayloadBytes = auditBuilder.Options.MaxPayloadBytes;
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

        // A record's and a batch's timestamps and the stale-group sweep read this clock, never the ambient one: a host
        // that registers its own (a test's FakeTimeProvider) keeps it.
        builder.Services.TryAddSingleton(TimeProvider.System);

        // A suppressed type's body must not reach the trail inside another message either: an [AuditAs] envelope member
        // holding one is left out of AuditJsonOptions' output. That lookup is process-wide (AuditJsonOptions is static).
        foreach (var type in auditBuilder.SuppressedTypes.Concat(auditBuilder.SuppressedPayloadTypes))
        {
            Conduit.Runtime.ConduitRuntime.SuppressAuditPayloadOf(type);
        }

        // Suppression is keyed by Type in the builder; the observer matches on the full type name
        // carried by DispatchInvoked observations, so resolve to type-name sets up front.
        var suppressedTypes = auditBuilder.SuppressedTypes
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);
        var suppressedPayloadTypes = auditBuilder.SuppressedPayloadTypes
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        // Observer (consolidates observations per dispatch). Registered as a singleton — one per DI
        // container, i.e. per host — and exposed via IConduitObserver, which the generated mediator
        // resolves from each dispatch's scope and attaches to the per-flow MessageContext. This keeps
        // observation isolated per host (no process-global static), so multiple hosts in one process
        // never cross-contaminate. The concrete type stays registered for the publisher's stale sweep.
        builder.Services.AddSingleton(serviceProvider =>
        {
            var serviceName = auditBuilder.Options.ServiceName
                ?? serviceProvider.GetService<IHostEnvironment>()?.ApplicationName
                ?? "unknown";
            return new AuditObserver(
                channel.Writer,
                serviceName,
                suppressedTypes,
                suppressedPayloadTypes,
                auditBuilder.Options.MaxPayloadBytes,
                serviceProvider.GetRequiredService<TimeProvider>());
        });
        builder.Services.AddSingleton<Conduit.Runtime.IConduitObserver>(
            serviceProvider => serviceProvider.GetRequiredService<AuditObserver>());

        // Background publisher (drains channel, publishes AuditBatch events). AddAudit runs inside AddConduit's callback,
        // which registers ConduitBackgroundDispatcher after it: the host stops hosted services in reverse order, so the
        // dispatcher's queued fire-and-forget work has run, and sealed its records, before this drains on stop.
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
    /// <param name="configure">Optional callback to tune the listener's retention. The persistence
    /// engine is configured separately on the builder (e.g. <c>conduit.UseSqlite(...)</c>).</param>
    /// <returns>The original <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">A retention option the callback set is
    /// invalid (<see cref="AuditOptions.Validate"/>); each failure names it.</exception>
    public static ConduitBuilder AddAuditListener(
        this ConduitBuilder builder,
        Action<AuditListenerBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var listenerBuilder = new AuditListenerBuilder(builder.Services);
        configure?.Invoke(listenerBuilder);
        listenerBuilder.Options.Validate();
        ValidateBoundOptionsOnStart(builder.Services);

        // Retention/purge are owned by the listener (it registers the purge service).
        builder.Services.Configure<AuditOptions>(options =>
        {
            options.RetentionPeriod = listenerBuilder.Options.RetentionPeriod;
            options.PurgeInterval = listenerBuilder.Options.PurgeInterval;
        });

        // The purge counts its retention back from this clock, as AddAudit's capture stamps records with it.
        builder.Services.TryAddSingleton(TimeProvider.System);

        return builder;
    }

    // The callback's options are checked at registration; a host can still set IOptions<AuditOptions> itself
    // (services.Configure<AuditOptions> after AddAudit), so the bound value is checked by the same rules as the host
    // starts, before the publisher's sweep or the purge reads it.
    private static void ValidateBoundOptionsOnStart(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AuditOptions>, AuditOptionsValidator>());
        services.AddOptions<AuditOptions>().ValidateOnStart();
    }
}
