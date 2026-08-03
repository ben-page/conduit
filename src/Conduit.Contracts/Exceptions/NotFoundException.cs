namespace Conduit.Exceptions;

/// <summary>
/// Thrown when a requested resource cannot be found. Maps to HTTP 404 Not Found.
/// </summary>
public sealed class NotFoundException(string message)
    : ConduitException(ErrorCode.NotFound, message);
