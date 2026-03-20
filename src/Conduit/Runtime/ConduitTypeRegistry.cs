namespace Conduit.Runtime;

/// <summary>
/// Compile-time registry of message types handled by this application.
/// Populated by the source generator in AddConduit(). Transports resolve
/// this from DI to discover which types need topology (subscriptions, bindings).
/// </summary>
public sealed class ConduitTypeRegistry
{
    public ConduitTypeRegistry(
        IReadOnlyList<Type> eventTypes,
        IReadOnlyList<Type> commandTypes,
        IReadOnlyList<Type> queryTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        ArgumentNullException.ThrowIfNull(commandTypes);
        ArgumentNullException.ThrowIfNull(queryTypes);

        this.EventTypes = eventTypes;
        this.CommandTypes = commandTypes;
        this.QueryTypes = queryTypes;

        this.EventTypeNames = eventTypes.Select(t => t.FullName!).ToList();
        this.CommandTypeNames = commandTypes.Select(t => t.FullName!).ToList();
        this.QueryTypeNames = queryTypes.Select(t => t.FullName!).ToList();
    }

    /// <summary>Event types with registered IEventListener implementations.</summary>
    public IReadOnlyList<Type> EventTypes { get; }

    /// <summary>Command types with registered ICommandHandler implementations.</summary>
    public IReadOnlyList<Type> CommandTypes { get; }

    /// <summary>Query types with registered IQueryHandler implementations.</summary>
    public IReadOnlyList<Type> QueryTypes { get; }

    /// <summary>Pre-computed FullName of each event type.</summary>
    public IReadOnlyList<string> EventTypeNames { get; }

    /// <summary>Pre-computed FullName of each command type.</summary>
    public IReadOnlyList<string> CommandTypeNames { get; }

    /// <summary>Pre-computed FullName of each query type.</summary>
    public IReadOnlyList<string> QueryTypeNames { get; }
}
