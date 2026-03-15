namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the caller lacks authorization. Maps to HTTP 401 Unauthorized.
/// </summary>
public sealed class UnauthorizedException(string message)
    : ConduitException(ErrorCode.Unauthorized, message);
