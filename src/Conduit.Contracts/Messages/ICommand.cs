namespace Conduit.Messages;

/// <summary>
/// Marker interface for commands that produce no result.
/// </summary>
public interface ICommand
{
    /// <summary>
    /// Validates preconditions before the handler executes. Override to add guards.
    /// Business validation belongs in the handler.
    /// </summary>
    public void Validate() { }
}

/// <summary>
/// Marker interface for commands that return a <typeparamref name="TResult"/>.
/// </summary>
/// <typeparam name="TResult">The type returned by the command handler.</typeparam>
public interface ICommand<TResult> : ICommand;
