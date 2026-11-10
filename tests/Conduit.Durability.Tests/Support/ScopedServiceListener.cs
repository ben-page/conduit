using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Durability.Tests.Support;

/// <summary>
/// A local envelope dispatcher whose listener depends on a scoped <typeparamref name="TService"/>: delivering an entry
/// resolves one from the entry's delivery scope, so disposing that scope disposes it. With a
/// <paramref name="failure"/>, the listener then throws it. Every type is local-only, so no transport is involved.
/// </summary>
public sealed class ScopedServiceListener<TService>(Exception? failure = null) : ILocalEnvelopeDispatcher
    where TService : notnull
{
    public bool DeliversLocally(string messageType) => true;

    public bool IsLocalOnly(string messageType) => true;

    public bool IsFireAndForget(string fullTypeName) => false;

    public bool IsEvent(string fullTypeName) => true;

    public Task PublishLocalEnvelopeAsync(DispatchEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        scopedServices.GetRequiredService<TService>();
        return failure is null ? Task.CompletedTask : Task.FromException(failure);
    }
}
