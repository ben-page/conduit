namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the caller lacks authorization. Maps to HTTP 401 Unauthorized.
/// Not sealed: subclass it for a refusal a client acts on, declaring the data as public properties
/// (<see cref="ConduitException.GetClientData"/>).
/// </summary>
public class UnauthorizedException(string message)
    : ConduitException(ErrorCode.Unauthorized, message);
