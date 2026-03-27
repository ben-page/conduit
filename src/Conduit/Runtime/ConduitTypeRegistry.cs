namespace Conduit.Runtime;

/// <summary>
/// Compile-time registry of message types handled by this application.
/// Populated by the source generator in AddConduit(). Transports resolve
/// this from DI to discover which types need topology (subscriptions, bindings).
/// </summary>
/// <remarks>
/// The registry can be filtered at runtime via <see cref="ExcludeCommandTypes"/>,
/// <see cref="ExcludeQueryTypes"/>, and <see cref="ExcludeEventTypes"/>. Filtered
/// properties (<see cref="ActiveCommandTypeNames"/>, etc.) reflect the exclusions.
/// The original unfiltered properties remain available for diagnostics.
/// </remarks>
public sealed class ConduitTypeRegistry
{
    /// <summary>
    /// Initializes the registry with the source-generated message type lists.
    /// </summary>
    /// <param name="eventTypes">All discovered event types.</param>
    /// <param name="commandTypes">All discovered command types.</param>
    /// <param name="queryTypes">All discovered query types.</param>
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

        // Active lists default to the full lists
        this.ActiveEventTypeNames = this.EventTypeNames;
        this.ActiveCommandTypeNames = this.CommandTypeNames;
        this.ActiveQueryTypeNames = this.QueryTypeNames;
    }

    /// <summary>All discovered event types (unfiltered).</summary>
    public IReadOnlyList<Type> EventTypes { get; }

    /// <summary>All discovered command types (unfiltered).</summary>
    public IReadOnlyList<Type> CommandTypes { get; }

    /// <summary>All discovered query types (unfiltered).</summary>
    public IReadOnlyList<Type> QueryTypes { get; }

    /// <summary>Pre-computed FullName of each event type (unfiltered).</summary>
    public IReadOnlyList<string> EventTypeNames { get; }

    /// <summary>Pre-computed FullName of each command type (unfiltered).</summary>
    public IReadOnlyList<string> CommandTypeNames { get; }

    /// <summary>Pre-computed FullName of each query type (unfiltered).</summary>
    public IReadOnlyList<string> QueryTypeNames { get; }

    /// <summary>
    /// Command type names after applying exclusion filters.
    /// Used by transports for topology creation.
    /// </summary>
    public IReadOnlyList<string> ActiveCommandTypeNames { get; private set; }

    /// <summary>
    /// Query type names after applying exclusion filters.
    /// Used by transports for topology creation.
    /// </summary>
    public IReadOnlyList<string> ActiveQueryTypeNames { get; private set; }

    /// <summary>
    /// Event type names after applying exclusion filters.
    /// Used by transports for topology creation.
    /// </summary>
    public IReadOnlyList<string> ActiveEventTypeNames { get; private set; }

    /// <summary>
    /// Excludes specific command types from topology creation.
    /// </summary>
    public void ExcludeCommandTypes(IEnumerable<Type> types)
    {
        var excluded = new HashSet<string>(
            types.Select(t => t.FullName!), StringComparer.Ordinal);
        this.ActiveCommandTypeNames = this.CommandTypeNames
            .Where(n => !excluded.Contains(n)).ToList();
    }

    /// <summary>
    /// Excludes specific query types from topology creation.
    /// </summary>
    public void ExcludeQueryTypes(IEnumerable<Type> types)
    {
        var excluded = new HashSet<string>(
            types.Select(t => t.FullName!), StringComparer.Ordinal);
        this.ActiveQueryTypeNames = this.QueryTypeNames
            .Where(n => !excluded.Contains(n)).ToList();
    }

    /// <summary>
    /// Excludes specific event types from topology creation.
    /// </summary>
    public void ExcludeEventTypes(IEnumerable<Type> types)
    {
        var excluded = new HashSet<string>(
            types.Select(t => t.FullName!), StringComparer.Ordinal);
        this.ActiveEventTypeNames = this.EventTypeNames
            .Where(n => !excluded.Contains(n)).ToList();
    }

    /// <summary>Excludes all command types from topology creation.</summary>
    public void ExcludeAllCommandTypes() => this.ActiveCommandTypeNames = [];

    /// <summary>Excludes all query types from topology creation.</summary>
    public void ExcludeAllQueryTypes() => this.ActiveQueryTypeNames = [];

    /// <summary>Excludes all event types from topology creation.</summary>
    public void ExcludeAllEventTypes() => this.ActiveEventTypeNames = [];
}
