namespace Conduit.Exceptions;

/// <summary>
/// Thrown when the caller is authenticated but not permitted to perform the action. Maps to
/// HTTP 403 Forbidden — distinct from <see cref="UnauthorizedException"/> (401, not authenticated),
/// so clients can show a "not allowed" message rather than forcing a re-login.
/// </summary>
public sealed class ForbiddenException(string message)
    : ConduitException(ErrorCode.Forbidden, message);
