namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the request conflicts with the current state. Maps to HTTP 409 Conflict.
/// A stale version (an ETag that no longer matches) is a <see cref="PreconditionFailedException"/> instead.
/// </summary>
public sealed class ConflictException(string message)
    : ConduitException(ErrorCode.Conflict, message);
