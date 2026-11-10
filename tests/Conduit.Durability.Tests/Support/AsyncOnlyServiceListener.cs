using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Durability.Tests.Support;

/// <summary>
/// A local envelope dispatcher whose listener depends on an <see cref="AsyncOnlyScopedService"/>: delivering an entry
/// resolves one from the entry's delivery scope, which therefore has to be disposed asynchronously. Every type is
/// local-only, so no transport is involved.
/// </summary>
public sealed class AsyncOnlyServiceListener : ILocalEnvelopeDispatcher
{
    public bool DeliversLocally(string messageType) => true;

    public bool IsLocalOnly(string messageType) => true;

    public bool IsFireAndForget(string fullTypeName) => false;

    public bool IsEvent(string fullTypeName) => true;

    public Task PublishLocalEnvelopeAsync(DispatchEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        scopedServices.GetRequiredService<AsyncOnlyScopedService>();
        return Task.CompletedTask;
    }
}
