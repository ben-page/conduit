using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Fluent builder for configuring the audit trail subsystem.
/// Exposes <see cref="AuditOptions"/> for tuning, plus per-type suppression methods.
/// </summary>
public sealed class AuditBuilder
{
    internal IServiceCollection Services { get; }
    internal AuditOptions Options { get; } = new();
    internal HashSet<Type> SuppressedTypes { get; } = [];
    internal HashSet<Type> SuppressedPayloadTypes { get; } = [];
    internal Action<object>? LocalStoreCallback { get; private set; }

    internal AuditBuilder(IServiceCollection services)
    {
        this.Services = services;
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
