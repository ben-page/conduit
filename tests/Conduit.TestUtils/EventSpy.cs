namespace Conduit.TestUtils;

/// <summary>
/// Collects events observed by listeners. Register as a singleton in DI
/// so each test host gets its own isolated instance.
/// Listeners inject <see cref="EventSpy{T}"/> and call <see cref="Record"/>
/// in HandleAsync. Tests use <see cref="WaitForCountAsync"/> to poll
/// for expected event counts rather than relying on Task.Delay.
/// </summary>
public class EventSpy<T>
{
    private readonly List<T> events = [];

    public void Record(T @event) => this.events.Add(@event);
    public IReadOnlyList<T> Events => this.events;
    public int Count => this.events.Count;

    /// <summary>
    /// Polls until the spy has recorded at least <paramref name="expected"/> events,
    /// or throws <see cref="TimeoutException"/> if the timeout elapses.
    /// </summary>
    public async Task WaitForCountAsync(int expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (this.events.Count < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Expected {expected} event(s) of type {typeof(T).Name} but received {this.events.Count}.");
            }

            await Task.Delay(10);
        }
    }
}
