using System.Text.Json;
using Conduit.Messages;
using Conduit.Runtime;

namespace Conduit.Jobs.Tests.Support;

/// <summary>Builds a <see cref="DispatchEnvelope"/> from an event, as the generated mediator does.</summary>
public static class TestEnvelope
{
    public static DispatchEnvelope For(IEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return new DispatchEnvelope(
            @event.GetType().FullName!,
            JsonSerializer.SerializeToElement(@event, @event.GetType(), ConduitRuntime.JsonOptions));
    }
}
