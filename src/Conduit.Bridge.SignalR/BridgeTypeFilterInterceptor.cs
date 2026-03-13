using Conduit.Dispatch;
using Conduit.Exceptions;
using Microsoft.Extensions.Options;

namespace Conduit.Bridge.SignalR;

/// <summary>
/// Rejects dispatch requests for message types not in the configured allowed set.
/// This prevents browser clients from dispatching internal-only commands.
/// </summary>
public class BridgeTypeFilterInterceptor(IOptions<BridgeOptions> options) : IDispatchInterceptor
{
    /// <inheritdoc />
    public Task<object?> InterceptRequestAsync(
        DispatchEnvelope envelope,
        Func<Task<object?>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(next);

        if (!options.Value.AllowedTypes.Contains(envelope.Type))
        {
            throw new UnauthorizedException(
                $"Type '{envelope.Type}' is not available via the bridge.");
        }

        return next();
    }

    /// <inheritdoc />
    public Task InterceptEventAsync(
        DispatchEnvelope envelope,
        Func<Task> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(next);
        // Events are server→client, no filtering needed
        return next();
    }
}
