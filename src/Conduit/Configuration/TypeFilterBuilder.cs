using Conduit.Dispatch;

namespace Conduit.Configuration;

/// <summary>
/// Fluent builder for configuring the type filter interceptor.
/// </summary>
public sealed class TypeFilterBuilder
{
    internal TypeFilterOptions Options { get; } = new();

    /// <summary>
    /// Allows the specified message type name to be dispatched through the filter.
    /// </summary>
    /// <param name="typeName">The full type name (e.g. "MyApp.Commands.CreateOrder").</param>
    /// <returns>This builder for chaining.</returns>
    public TypeFilterBuilder AllowType(string typeName)
    {
        this.Options.AllowedTypes.Add(typeName);
        return this;
    }
}
