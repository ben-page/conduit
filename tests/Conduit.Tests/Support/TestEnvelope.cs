using System.Text.Json;
using Conduit.Messages;
using Conduit.Runtime;

namespace Conduit.Tests.Support;

/// <summary>Builds a <see cref="DispatchEnvelope"/> from a message instance, as the generated mediator does.</summary>
public static class TestEnvelope
{
    public static DispatchEnvelope For(IEvent @event) => new(
        @event.GetType().FullName!,
        JsonSerializer.SerializeToElement(@event, @event.GetType(), ConduitRuntime.JsonOptions));

    /// <summary>Builds an envelope for a command or query, as the generated mediator does.</summary>
    public static DispatchEnvelope ForRequest(object request) => new(
        request.GetType().FullName!,
        JsonSerializer.SerializeToElement(request, request.GetType(), ConduitRuntime.JsonOptions));
}
