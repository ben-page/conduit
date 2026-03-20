using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.Exceptions;
using Microsoft.Extensions.Options;

namespace Conduit.Middleware;

/// <summary>
/// Rejects dispatch requests for message types not in the configured allowed set.
/// This prevents browser clients from dispatching internal-only commands.
/// </summary>
public class TypeFilterInterceptor(IOptions<TypeFilterOptions> options) : IWireInterceptor
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
                $"Type '{envelope.Type}' is not allowed by the type filter.");
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
        // Events flow both directions but are not filtered — type filter applies to commands/queries only
        return next();
    }
}
