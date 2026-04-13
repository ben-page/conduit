using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Audit.Runtime;

/// <summary>
/// A batch of consolidated audit records published as a fire-and-forget event.
/// Contains records from a single service host, grouped by the <see cref="AuditBatchPublisher"/>
/// batching window.
/// </summary>
[FireAndForget]
public sealed record AuditBatch(
    string SourceService,
    string SourceHost,
    DateTimeOffset BatchTimestamp,
    IReadOnlyList<DispatchAuditRecord> Records) : IEvent;
