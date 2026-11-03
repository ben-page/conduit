using Conduit.Exceptions;
using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>An inbound check that always refuses with a refusal naming its sentence, so a preflight's answer can be seen rendered.</summary>
public sealed class PreflightTextCheckBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>, IInboundCheckBehavior
{
    public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken) =>
        throw new ConflictException("Order was already shipped") { Text = new RefusalText("orders.shipped") };
}
