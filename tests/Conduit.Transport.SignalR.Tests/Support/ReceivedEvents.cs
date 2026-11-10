using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>
/// The event type names one client connection received, in arrival order. <see cref="WaitForAsync"/> completes once a
/// given type has arrived; a hub delivers one connection's messages in order, so every event pushed to it before that one
/// has arrived too.
/// </summary>
public sealed class ReceivedEvents
{
    private readonly ConcurrentQueue<string> received = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> arrivals = new(StringComparer.Ordinal);

    private ReceivedEvents()
    {
    }

    public IReadOnlyList<string> Types => [.. this.received];

    /// <summary>Listens on <paramref name="connection"/> for each of <paramref name="eventTypes"/>, by full type name.</summary>
    public static ReceivedEvents Listen(HubConnection connection, params Type[] eventTypes)
    {
        var events = new ReceivedEvents();
        foreach (var eventType in eventTypes)
        {
            var name = eventType.FullName!;
            connection.On<JsonElement>(name, _ =>
            {
                events.received.Enqueue(name);
                events.Arrival(name).TrySetResult();
            });
        }

        return events;
    }

    public Task WaitForAsync(Type eventType)
    {
        return this.Arrival(eventType.FullName!).Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private TaskCompletionSource Arrival(string name)
    {
        return this.arrivals.GetOrAdd(
            name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
