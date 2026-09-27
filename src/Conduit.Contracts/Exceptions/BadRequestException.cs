namespace Conduit.Exceptions;

/// <summary>
/// Thrown when a request is malformed or fails a precondition that is the caller's fault.
/// Maps to HTTP 400 via <see cref="ErrorCode.BadRequest"/>.
/// Not sealed: subclass it for a refusal a client acts on, optionally declaring its data as public
/// properties (<see cref="ConduitException.GetClientData"/>); a data-less subclass declared outside
/// Conduit.Contracts is still client-typed.
/// </summary>
public class BadRequestException(string message)
    : ConduitException(ErrorCode.BadRequest, message);
