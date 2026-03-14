namespace Conduit.Messages;

/// <summary>
/// Represents the absence of a return value. Used as <c>TResult</c> for void
/// commands so they can share the <c>IPipelineBehavior&lt;TRequest, TResult&gt;</c>
/// pipeline without boxing or null. A single shared instance is used everywhere.
/// </summary>
public sealed class Unit
{
    /// <summary>
    /// The shared instance. There is only ever one.
    /// </summary>
    public static readonly Unit Value = new();

    private Unit() { }
}
