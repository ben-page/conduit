using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Handlers;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Fluent builder for the audit listener — the side that consumes <see cref="AuditBatch"/> events
/// and persists them. The events may originate in this process (when paired with <c>AddAudit</c>)
/// or arrive over a transport from other services (a central collector); the listener handles both
/// identically. Tunes retention; the database engine is configured separately on the
/// <see cref="Conduit.Configuration.ConduitBuilder"/> (e.g. <c>conduit.UseSqlite(...)</c>) and shared
/// by all persistence modules via DI (<c>IConduitConnectionFactory</c> + <c>ISqlDialect</c>).
/// </summary>
public sealed class AuditListenerBuilder
{
    internal IServiceCollection Services { get; }
    internal AuditOptions Options { get; } = new();

    internal AuditListenerBuilder(IServiceCollection services)
    {
        this.Services = services;

        // The persistence engine (IConduitConnectionFactory + ISqlDialect) is resolved from DI; the
        // AuditTableInitializer fails fast at startup if no engine is registered. The store is Scoped
        // because IConduitConnectionFactory is Scoped — the purge service and dispatch path both
        // resolve it inside a DI scope.
        this.Services.AddScoped<IAuditStore, DbAuditStore>();
        this.Services.AddTransient<IEventListener<AuditBatch>, LocalAuditListener>();

        // The generator never saw that listener ([NotAutoRegistered]), so a broker transport would not subscribe
        // AuditBatch for a central collector: it is activated in the type registry, which the generated AddConduit
        // registers before this runs. (A builder made by hand has no registry.)
        var typeRegistry = this.Services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(ConduitTypeRegistry) && !descriptor.IsKeyedService)
            ?.ImplementationInstance as ConduitTypeRegistry;
        typeRegistry?.ActivateEvent(typeof(AuditBatch).FullName!);
        this.Services.AddHostedService<AuditTableInitializer>();
        this.Services.AddHostedService<AuditPurgeService>();
    }

    /// <summary>How long stored audit records are retained before the purge removes them. Positive, and at most 3650 days. Default 90 days.</summary>
    public TimeSpan RetentionPeriod
    {
        get => this.Options.RetentionPeriod;
        set => this.Options.RetentionPeriod = value;
    }

    /// <summary>How often the purge runs to remove expired records. Positive, and at most one day. Default 24 hours.</summary>
    public TimeSpan PurgeInterval
    {
        get => this.Options.PurgeInterval;
        set => this.Options.PurgeInterval = value;
    }
}
