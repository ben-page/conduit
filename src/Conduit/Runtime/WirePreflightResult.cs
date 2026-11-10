using System.ComponentModel;

namespace Conduit.Runtime;

/// <summary>
/// The wire's answer to a request, short of its handler (<see cref="ConduitRuntime.PreflightWireRequestAsync"/>): admitted,
/// refused, or neither -- an inbound gate discarded it, which the wire answers with no error and no data.
/// </summary>
/// <param name="Admitted">True when the request passed every gate and then either read as its contract and ran through
/// every inbound check, or is routed to another endpoint: the wire forwards that unread, so it is admitted with neither
/// (its checks are the receiving host's).</param>
/// <param name="Refusal">The error the wire would answer with -- a refusal, or an unexpected failure mapped as the wire maps
/// it (<c>InternalError</c> with its reference, or a translator's refusal); null when admitted or discarded.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record WirePreflightResult(bool Admitted, DispatchError? Refusal);
