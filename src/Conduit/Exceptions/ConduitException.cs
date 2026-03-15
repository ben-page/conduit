namespace Conduit.Exceptions;

/// <summary>
/// Base class for domain exceptions that carry a machine-readable error code
/// for automatic wire-level error translation.
/// </summary>
public abstract class ConduitException(ErrorCode code, string message)
    : Exception(message)
{
    /// <summary>
    /// The machine-readable error code with HTTP status mapping.
    /// </summary>
    public ErrorCode Code => code;
}
