using Conduit.Configuration;
using Conduit.Exceptions;
using Microsoft.Extensions.Options;

namespace Conduit.Middleware;

/// <summary>
/// Rejects dispatch requests for message types not in the configured allowed set.
/// This prevents browser clients from dispatching internal-only commands.
/// Throws <see cref="ForbiddenException"/> (403) for rejected types rather than returning
/// false — a blocked type is an authenticated-but-not-permitted error, not a silent discard.
/// Deliberately NOT <see cref="UnauthorizedException"/> (401): the caller IS authenticated;
/// the type is simply not client-dispatchable, so re-authenticating would not help. A client
/// that maps 401 to a forced re-login must not be bounced to /login by a type-filter block
/// (e.g. a version-skewed bundle dispatching a since-removed type) — 403 lets it surface a
/// graceful error and keep the session.
/// </summary>
public class TypeFilterInterceptor(IOptions<TypeFilterOptions> options) : IInboundInterceptor
{
    /// <inheritdoc />
    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        if (!options.Value.AllowedTypes.Contains(messageType))
        {
            throw new ForbiddenException(
                $"Type '{messageType}' is not allowed by the type filter.");
        }

        return Task.FromResult(true);
    }
}
