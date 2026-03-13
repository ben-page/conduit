namespace Conduit.Exceptions;

/// <summary>
/// Thrown when a request fails validation. Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public class ValidationException(string message)
    : ConduitException("Validation", 422, message);
