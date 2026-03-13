namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the caller lacks authorization. Maps to HTTP 401 Unauthorized.
/// </summary>
public class UnauthorizedException(string message)
    : ConduitException("Unauthorized", 401, message);
