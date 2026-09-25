namespace Conduit.Exceptions;

/// <summary>
/// Thrown when a requested resource cannot be found. Maps to HTTP 404 Not Found.
/// Not sealed: subclass it for a refusal a client acts on, declaring the data as public properties
/// (<see cref="ConduitException.GetClientData"/>).
/// </summary>
public class NotFoundException(string message)
    : ConduitException(ErrorCode.NotFound, message);
