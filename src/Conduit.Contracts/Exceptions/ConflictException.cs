namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the request conflicts with the current state. Maps to HTTP 409 Conflict.
/// A stale version (an ETag that no longer matches) is a <see cref="PreconditionFailedException"/> instead.
/// Not sealed: subclass it for a refusal a client acts on, declaring the data as public properties
/// (<see cref="ConduitException.GetClientData"/>).
/// </summary>
public class ConflictException(string message)
    : ConduitException(ErrorCode.Conflict, message);
