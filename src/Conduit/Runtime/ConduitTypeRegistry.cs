using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Conduit.Messages;

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
/// <para>
/// Events are listed twice over: <see cref="EventTypeNames"/> is every event the generator discovered, and
/// <see cref="ActiveEventTypeNames"/> is the events this host listens for -- those with a listener the generator found,
/// plus those <see cref="ActivateEvent"/> adds for a listener registered at runtime (a <c>Conduit.Jobs</c> event
/// trigger, a <c>HandleEvent</c> override). Broker transports subscribe the active list.
/// </para>
/// <para>
/// Every registry also adds its message types to a process-wide lookup by <see cref="Type.FullName"/>
/// (<see cref="TryGetRegisteredMessageType"/>), which <see cref="ConduitRuntime.AuditJsonOptions"/> reads to type an
/// <c>[AuditAs]</c> member: it is static, so it cannot resolve a registry from DI. Exclusions do not apply to it
/// (they filter topology, not what a host can dispatch), and only a type implementing <c>ICommand</c>,
/// <c>IQuery&lt;T&gt;</c> or <c>IEvent</c> is added. A name two different types register resolves to nothing.
/// </para>
/// </remarks>
public sealed class ConduitTypeRegistry
{
    private static readonly ConcurrentDictionary<string, Type> RegisteredMessageTypes = new(StringComparer.Ordinal);

    // Guards every write of ActiveEventTypeNames. Each write publishes a new list, so a reader holds a consistent
    // snapshot without taking it.
    private readonly Lock activeEventsGate = new();

    /// <summary>
    /// Initializes the registry with the source-generated message type lists.
    /// </summary>
    /// <param name="eventTypes">All discovered event types.</param>
    /// <param name="commandTypes">All discovered command types.</param>
    /// <param name="queryTypes">All discovered query types.</param>
    /// <param name="listenedEventTypes">
    /// The event types a listener is registered for at compile time: the initial <see cref="ActiveEventTypeNames"/>.
    /// Null means every one of <paramref name="eventTypes"/>.
    /// </param>
    /// <param name="clientVisibleEventTypes">The discovered event types marked <c>[ClientVisible]</c>, on themselves or a base.</param>
    public ConduitTypeRegistry(
        IReadOnlyList<Type> eventTypes,
        IReadOnlyList<Type> commandTypes,
        IReadOnlyList<Type> queryTypes,
        IReadOnlyList<Type>? listenedEventTypes = null,
        IReadOnlyList<Type>? clientVisibleEventTypes = null)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        ArgumentNullException.ThrowIfNull(commandTypes);
        ArgumentNullException.ThrowIfNull(queryTypes);

        this.EventTypes = eventTypes;
        this.CommandTypes = commandTypes;
        this.QueryTypes = queryTypes;

        this.EventTypeNames = eventTypes.Select(t => t.FullName!).ToFrozenSet(StringComparer.Ordinal);
        this.ClientVisibleEventTypeNames = (clientVisibleEventTypes ?? []).Select(t => t.FullName!).ToFrozenSet(StringComparer.Ordinal);
        this.CommandTypeNames = commandTypes.Select(t => t.FullName!).ToList();
        this.QueryTypeNames = queryTypes.Select(t => t.FullName!).ToList();

        // Active lists default to the full lists; for events, to the ones a listener is registered for
        this.ActiveEventTypeNames = (listenedEventTypes ?? eventTypes).Select(t => t.FullName!).Distinct(StringComparer.Ordinal).ToList();
        this.ActiveCommandTypeNames = this.CommandTypeNames;
        this.ActiveQueryTypeNames = this.QueryTypeNames;

        foreach (var type in eventTypes.Concat(commandTypes).Concat(queryTypes))
        {
            if (type.FullName is { } fullName && IsMessageType(type))
            {
                // A name two different types claim (two versions of a contracts assembly, separate load contexts) is
                // ambiguous: capture could redact by one type's marks while dispatch runs the other, so it resolves to
                // nothing (the placeholder) rather than to either.
                RegisteredMessageTypes.AddOrUpdate(fullName, type, (_, existing) => existing == type ? existing : typeof(AmbiguousName));
            }
        }
    }

    /// <summary>All discovered event types (unfiltered), whether or not this host listens for them.</summary>
    public IReadOnlyList<Type> EventTypes { get; }

    /// <summary>All discovered command types (unfiltered).</summary>
    public IReadOnlyList<Type> CommandTypes { get; }

    /// <summary>All discovered query types (unfiltered).</summary>
    public IReadOnlyList<Type> QueryTypes { get; }

    /// <summary>
    /// FullName of every discovered event type (unfiltered), whether or not this host listens for it. Answers "is this
    /// wire type an event"; <see cref="ActiveEventTypeNames"/> answers "does this host subscribe it".
    /// </summary>
    public IReadOnlySet<string> EventTypeNames { get; }

    /// <summary>FullName of every discovered event type marked <c>[ClientVisible]</c>, on itself or a base.</summary>
    public IReadOnlySet<string> ClientVisibleEventTypeNames { get; }

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
    /// The event types this host listens for: those with a listener the generator found, plus those
    /// <see cref="ActivateEvent"/> added, less any exclusions. Used by transports for topology creation.
    /// </summary>
    public IReadOnlyList<string> ActiveEventTypeNames { get; private set; }

    /// <summary>
    /// Adds an event type to <see cref="ActiveEventTypeNames"/>, for a listener registered at runtime, which the
    /// generator never saw (a <c>Conduit.Jobs</c> event trigger, a <c>HandleEvent</c> override). Idempotent and
    /// thread-safe. Call it before the transports start: they read the active list once, at startup.
    /// </summary>
    /// <param name="fullTypeName">The event's <see cref="Type.FullName"/>.</param>
    public void ActivateEvent(string fullTypeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullTypeName);

        lock (this.activeEventsGate)
        {
            if (!this.ActiveEventTypeNames.Contains(fullTypeName, StringComparer.Ordinal))
            {
                this.ActiveEventTypeNames = [.. this.ActiveEventTypeNames, fullTypeName];
            }
        }
    }

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
    /// Excludes specific event types from topology creation: removes them from <see cref="ActiveEventTypeNames"/>.
    /// </summary>
    public void ExcludeEventTypes(IEnumerable<Type> types)
    {
        var excluded = new HashSet<string>(
            types.Select(t => t.FullName!), StringComparer.Ordinal);
        lock (this.activeEventsGate)
        {
            this.ActiveEventTypeNames = this.ActiveEventTypeNames
                .Where(n => !excluded.Contains(n)).ToList();
        }
    }

    /// <summary>
    /// Resolves a wire type name (<see cref="Type.FullName"/>) to a message type some registry in this process
    /// registered. Never loads a type by name: a name no registry listed resolves to nothing.
    /// </summary>
    internal static bool TryGetRegisteredMessageType(string fullName, [NotNullWhen(true)] out Type? type)
    {
        if (RegisteredMessageTypes.TryGetValue(fullName, out type) && type != typeof(AmbiguousName))
        {
            return true;
        }

        type = null;
        return false;
    }

    // Stands in the lookup for a name two different message types registered.
    private sealed class AmbiguousName;

    private static bool IsMessageType(Type type) =>
        typeof(ICommand).IsAssignableFrom(type)
        || typeof(IEvent).IsAssignableFrom(type)
        || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>));

    /// <summary>Excludes all command types from topology creation.</summary>
    public void ExcludeAllCommandTypes() => this.ActiveCommandTypeNames = [];

    /// <summary>Excludes all query types from topology creation.</summary>
    public void ExcludeAllQueryTypes() => this.ActiveQueryTypeNames = [];

    /// <summary>Excludes all event types from topology creation.</summary>
    public void ExcludeAllEventTypes()
    {
        lock (this.activeEventsGate)
        {
            this.ActiveEventTypeNames = [];
        }
    }
}
