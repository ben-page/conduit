namespace Conduit.Exceptions;

/// <summary>
/// Thrown when a requested resource cannot be found. Maps to HTTP 404 Not Found.
/// </summary>
public class NotFoundException(string message)
    : ConduitException("NotFound", 404, message);
