namespace Conduit.Exceptions;

/// <summary>
/// Thrown when a request is malformed or fails a precondition that is the caller's fault.
/// Maps to HTTP 400 via <see cref="ErrorCode.BadRequest"/>.
/// </summary>
public sealed class BadRequestException(string message)
    : ConduitException(ErrorCode.BadRequest, message);
