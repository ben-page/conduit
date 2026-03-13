namespace Conduit.Exceptions;

/// <summary>
/// Base class for domain exceptions that carry a machine-readable error code
/// and an HTTP status code for automatic wire-level error translation.
/// </summary>
public abstract class ConduitException(string code, int statusCode, string message)
    : Exception(message)
{
    /// <summary>
    /// A machine-readable error code (e.g. "Validation", "NotFound").
    /// </summary>
    public string Code => code;

    /// <summary>
    /// The HTTP status code to return when this exception crosses a wire boundary.
    /// </summary>
    public int StatusCode => statusCode;
}
