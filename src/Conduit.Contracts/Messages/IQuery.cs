namespace Conduit.Messages;

/// <summary>
/// Marker interface for queries that return a <typeparamref name="TResult"/>.
/// </summary>
/// <typeparam name="TResult">The type returned by the query handler.</typeparam>
public interface IQuery<TResult>
{
    /// <summary>
    /// Validates preconditions before the handler executes. Override to add guards.
    /// Business validation belongs in the handler.
    /// </summary>
    public void Validate() { }
}
