namespace Conduit.Runtime;

/// <summary>
/// Compile-time registry of message types handled by this application.
/// Populated by the source generator in AddConduit(). Transports resolve
/// this from DI to discover which types need topology (subscriptions, bindings).
/// </summary>
public sealed class ConduitTypeRegistry
{
    public ConduitTypeRegistry(
        IReadOnlyList<Type> eventListeners,
        IReadOnlyList<Type> commandHandlers,
        IReadOnlyList<Type> queryHandlers)
    {
        ArgumentNullException.ThrowIfNull(eventListeners);
        ArgumentNullException.ThrowIfNull(commandHandlers);
        ArgumentNullException.ThrowIfNull(queryHandlers);

        this.EventListeners = eventListeners;
        this.CommandHandlers = commandHandlers;
        this.QueryHandlers = queryHandlers;
    }

    /// <summary>Event types with registered IEventListener implementations.</summary>
    public IReadOnlyList<Type> EventListeners { get; }

    /// <summary>Command types with registered ICommandHandler implementations.</summary>
    public IReadOnlyList<Type> CommandHandlers { get; }

    /// <summary>Query types with registered IQueryHandler implementations.</summary>
    public IReadOnlyList<Type> QueryHandlers { get; }

    /// <summary>Returns FullName of each event listener type.</summary>
    public IReadOnlyList<string> GetEventListenerNames() =>
        this.EventListeners.Select(t => t.FullName!).ToList();

    /// <summary>Returns FullName of each command handler type.</summary>
    public IReadOnlyList<string> GetCommandHandlerNames() =>
        this.CommandHandlers.Select(t => t.FullName!).ToList();

    /// <summary>Returns FullName of each query handler type.</summary>
    public IReadOnlyList<string> GetQueryHandlerNames() =>
        this.QueryHandlers.Select(t => t.FullName!).ToList();

    /// <summary>Returns FullName of all handled types (events + commands + queries).</summary>
    public IReadOnlyList<string> GetAllHandledTypeNames() =>
        this.EventListeners.Concat(this.CommandHandlers).Concat(this.QueryHandlers)
            .Select(t => t.FullName!).ToList();
}
