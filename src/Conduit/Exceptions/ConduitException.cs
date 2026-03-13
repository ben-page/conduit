namespace Conduit.Exceptions;

/// <summary>
/// Base class for domain exceptions that carry a machine-readable error code
/// for automatic wire-level error translation.
/// </summary>
public abstract class ConduitException(string code, string message)
    : Exception(message)
{
    /// <summary>
    /// A machine-readable error code (e.g. "Conflict", "NotFound").
    /// </summary>
    public string Code => code;
}
