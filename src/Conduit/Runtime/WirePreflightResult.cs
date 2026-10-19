namespace Conduit.Runtime;

/// <summary>
/// The wire's answer to a request, short of its handler (<see cref="ConduitRuntime.PreflightWireRequestAsync"/>): admitted,
/// refused, or neither -- an inbound gate discarded it, which the wire answers with no error and no data.
/// </summary>
/// <param name="Admitted">True when the request passed every gate, read as its contract, and ran through every inbound
/// check.</param>
/// <param name="Refusal">The error the wire would answer with -- a refusal, or an unexpected failure mapped as the wire maps
/// it (<c>InternalError</c> with its reference, or a translator's refusal); null when admitted or discarded.</param>
public sealed record WirePreflightResult(bool Admitted, DispatchError? Refusal);
