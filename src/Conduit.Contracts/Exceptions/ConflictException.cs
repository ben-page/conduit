namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the request conflicts with the current state. Maps to HTTP 409 Conflict.
/// A stale version (an ETag that no longer matches) is a <see cref="PreconditionFailedException"/> instead.
/// Not sealed: subclass it for a refusal a client acts on, optionally declaring its data as public
/// properties (<see cref="ConduitException.GetClientData"/>); a data-less subclass declared outside
/// Conduit.Contracts is still client-typed.
/// </summary>
public class ConflictException(string message)
    : ConduitException(ErrorCode.Conflict, message);
