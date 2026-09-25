namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the caller is authenticated but not permitted to perform the action. Maps to
/// HTTP 403 Forbidden — distinct from <see cref="UnauthorizedException"/> (401, not authenticated),
/// so clients can show a "not allowed" message rather than forcing a re-login.
/// Not sealed: subclass it for a refusal a client acts on, declaring the data as public properties
/// (<see cref="ConduitException.GetClientData"/>).
/// </summary>
public class ForbiddenException(string message)
    : ConduitException(ErrorCode.Forbidden, message);
