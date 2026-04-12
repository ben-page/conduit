namespace Conduit.Runtime;

/// <summary>
/// Receives observation records emitted during message dispatch.
/// Implementations collect, transform, or forward observations for audit trails,
/// metrics, or diagnostics.
/// </summary>
public interface IConduitObserver
{
    /// <summary>
    /// Called when an observation occurs during message dispatch.
    /// </summary>
    /// <param name="observation">The observation record.</param>
    /// <param name="context">The current message context at the time of observation.</param>
    public void OnObservation(MessageObservation observation, MessageContext context);
}
