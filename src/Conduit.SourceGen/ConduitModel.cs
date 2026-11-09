using Conduit.Analysis;

namespace Conduit.SourceGen;

/// <summary>
/// The complete, value-equatable result of discovery. Produced by <c>ConduitGenerator.BuildModel</c>
/// from the compilation walk and consumed by <c>ConduitGenerator.Emit</c>. Holds only strings, enums,
/// bools, and <see cref="EquatableArray{T}"/> — never an <c>ISymbol</c> or <c>Compilation</c> — so
/// Roslyn can compare it by value and skip re-emission when discovery is unchanged.
/// </summary>
public sealed record ConduitModel
{
    /// <summary>All discovered dispatchable command/query/event types, unique by full type name.</summary>
    public required EquatableArray<DispatchableTypeInfo> DispatchableTypes { get; init; }

    /// <summary>Generic message types, as declared (e.g. <c>Ns.Wrap&lt;T&gt;</c>), none of them emitted (CONDUIT007).</summary>
    public required EquatableArray<string> GenericMessageTypes { get; init; }

    /// <summary>Message full type names declared by more than one assembly, none of them emitted (CONDUIT008).</summary>
    public required EquatableArray<DuplicateMessageType> DuplicateMessageTypes { get; init; }

    /// <summary>Types a message carries whose [AuditAs] is misapplied (CONDUIT006), one per type.</summary>
    public required EquatableArray<CarriedAuditAsMisuse> CarriedAuditAsMisuses { get; init; }

    /// <summary>All discovered accessible command/query handlers.</summary>
    public required EquatableArray<HandlerInfo> Handlers { get; init; }

    /// <summary>Event listener groups (with [LocalOnly] resolved).</summary>
    public required EquatableArray<ListenerGroup> ListenerGroups { get; init; }

    /// <summary>Message types whose only handler is inaccessible to the generating assembly.</summary>
    public required EquatableArray<string> InaccessibleHandlerMessageTypes { get; init; }

    /// <summary>Namespace patterns from <c>[assembly: RemoteContracts(...)]</c> (suppress CONDUIT001).</summary>
    public required EquatableArray<string> RemoteContractPatterns { get; init; }

    /// <summary>True when Conduit.Transport.Http is referenced (emit REST endpoints).</summary>
    public required bool HasHttpTransport { get; init; }

    /// <summary>True when Conduit.Resilience is referenced (emit resilience dispatch).</summary>
    public required bool HasResilience { get; init; }
}
