using System.Text.Json;
using Conduit.Handlers;
using Conduit.Messages;
using Conduit.Runtime;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

/// <summary>
/// A command whose handler sends an <see cref="InnerCommand"/> through its own injected <see cref="IWireMediator"/>,
/// under a context it starts first, the way a batch dispatcher hands each item to the wire: a nested dispatch. The
/// handler then throws <see cref="RetryMeException"/> on its first <paramref name="Failures"/> calls.
/// </summary>
public record WireNestingCommand(int Failures) : ICommand;

public class WireNestingCommandHandler(IWireMediator wireMediator, RetryProbe probe, AttemptProbe scope)
    : ICommandHandler<WireNestingCommand>
{
    public async Task HandleAsync(WireNestingCommand command, CancellationToken cancellationToken)
    {
        var call = probe.See(nameof(WireNestingCommand), scope);

        // The item's own dispatch: a context of its own, caused by this one, as an in-process wire call requires.
        var previous = MessageContext.Current;
        MessageContext.StartDispatch();
        try
        {
            var result = await wireMediator.HandleWireRequestAsync(
                new DispatchEnvelope(
                    typeof(InnerCommand).FullName!,
                    JsonSerializer.SerializeToElement(new InnerCommand(Failures: 0), ConduitRuntime.JsonOptions)),
                cancellationToken);
            if (result.Error is { } error)
            {
                throw new InvalidOperationException($"The item failed: {error.Message}");
            }
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }

        if (call <= command.Failures)
        {
            throw new RetryMeException($"{nameof(WireNestingCommand)} failed on call {call}");
        }
    }
}
