namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the request conflicts with the current state. Maps to HTTP 409 Conflict.
/// </summary>
public class ConflictException(string message)
    : ConduitException("Conflict", 409, message);
