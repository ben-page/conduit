using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Audit.Runtime;

/// <summary>
/// A batch of consolidated audit records published as a fire-and-forget event.
/// Contains records from a single service host, grouped by the <see cref="AuditBatchPublisher"/>
/// batching window.
/// </summary>
/// <remarks>
/// Marked <c>[DoNotAudit]</c> to prevent the audit infrastructure from auditing its own
/// internal events, which would create an infinite feedback loop (audit batch publishes
/// generate observations that create more audit batches).
/// Not marked <c>[FireAndForget]</c> because the publisher already runs in a background
/// service — running listeners inline avoids scope lifetime issues when the mediator
/// is resolved from a scoped provider that is disposed after <c>PublishAsync</c> returns.
/// </remarks>
[DoNotAudit]
public sealed record AuditBatch(
    string SourceService,
    string SourceHost,
    DateTime BatchTimestamp,
    IReadOnlyList<DispatchAuditRecord> Records) : IEvent;
