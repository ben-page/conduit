namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the caller lacks authorization. Maps to HTTP 401 Unauthorized.
/// Not sealed: subclass it for a refusal a client acts on, optionally declaring its data as public
/// properties (<see cref="ConduitException.GetClientData"/>); a data-less subclass declared outside
/// Conduit.Contracts is still client-typed.
/// </summary>
public class UnauthorizedException(string message)
    : ConduitException(ErrorCode.Unauthorized, message);
