using Conduit.Runtime;

namespace Conduit.Durability.Tests.Support;

/// <summary>
/// An <see cref="ILocalEnvelopeDispatcher"/> that classifies the given types as <c>[FireAndForget]</c> and owns no
/// local delivery, as the generated one does for a routed fire-and-forget command.
/// </summary>
internal sealed class FireAndForgetClassifier(params string[] fireAndForgetTypes) : ILocalEnvelopeDispatcher
{
    public bool DeliversLocally(string messageType) => false;

    public bool IsLocalOnly(string messageType) => false;

    public bool IsFireAndForget(string fullTypeName) => fireAndForgetTypes.Contains(fullTypeName);

    public bool IsEvent(string fullTypeName) => false;

    public Task PublishLocalEnvelopeAsync(DispatchEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken)
        => throw new InvalidOperationException("A routed command has no local delivery.");
}
