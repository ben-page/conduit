namespace Conduit.Runtime;

/// <summary>
/// Carries caller-supplied properties through the dispatch pipeline.
/// Observers and audit infrastructure read these to enrich recorded entries.
/// </summary>
public sealed class DispatchOptions
{
    /// <summary>
    /// User-defined key/value pairs attached to the dispatch. Names are case-insensitive, as
    /// <see cref="MessageContext.Properties"/>' are.
    /// </summary>
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds or overwrites a property on this dispatch.
    /// </summary>
    /// <param name="key">The property name.</param>
    /// <param name="value">The property value (may be <see langword="null"/>).</param>
    /// <returns>This instance, for fluent chaining.</returns>
    public DispatchOptions Tag(string key, object? value)
    {
        this.Properties[key] = value;
        return this;
    }
}
