using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

/// <summary>
/// The request the preflight tests judge. <paramref name="Verdict"/> tells <see cref="PreflightCheckBehavior{TRequest, TResult}"/>
/// what to do: <c>admit</c>, <c>refuse</c> (a typed refusal), <c>explode</c> (an unexpected exception) or <c>translate</c>
/// (an exception <see cref="PreflightTranslator"/> claims).
/// </summary>
public record PreflightCommand(string Verdict) : ICommand<string>;

/// <summary>Throws if called: a preflight never runs the handler.</summary>
public class PreflightCommandHandler : ICommandHandler<PreflightCommand, string>
{
    public Task<string> HandleAsync(PreflightCommand command, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"{nameof(PreflightCommand)}'s handler ran.");
}
