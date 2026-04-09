using Conduit.Configuration;
using Conduit.Exceptions;
using Microsoft.Extensions.Options;

namespace Conduit.Middleware;

/// <summary>
/// Rejects dispatch requests for message types not in the configured allowed set.
/// This prevents browser clients from dispatching internal-only commands.
/// Throws <see cref="UnauthorizedException"/> for rejected types rather than
/// returning false — unauthorized access is an error, not a silent discard.
/// </summary>
public class TypeFilterInterceptor(IOptions<TypeFilterOptions> options) : IInboundInterceptor
{
    /// <inheritdoc />
    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        if (!options.Value.AllowedTypes.Contains(messageType))
        {
            throw new UnauthorizedException(
                $"Type '{messageType}' is not allowed by the type filter.");
        }

        return Task.FromResult(true);
    }
}
