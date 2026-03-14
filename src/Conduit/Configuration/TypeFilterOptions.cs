namespace Conduit.Configuration;

/// <summary>
/// Configuration options for the type filter interceptor, controlling which message types
/// are allowed through from external clients.
/// </summary>
public sealed class TypeFilterOptions
{
    /// <summary>
    /// The set of message type names that external clients are permitted to dispatch.
    /// Types not in this set are rejected by the <see cref="Middleware.TypeFilterInterceptor"/>.
    /// </summary>
    public HashSet<string> AllowedTypes { get; } = [];
}
