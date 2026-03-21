using System.Collections.Concurrent;

namespace Conduit.Transport.IntegrationTests.Support;

public sealed class SpyLog
{
    private readonly ConcurrentQueue<object> invocations = new();

    public void Record(object message) => this.invocations.Enqueue(message);

    public IReadOnlyList<object> Invocations => [.. this.invocations];

    public int Count => this.invocations.Count;

    public async Task WaitForCountAsync(int expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (this.invocations.Count < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Expected {expected} invocations but got {this.invocations.Count} after timeout");
            }

            await Task.Delay(50);
        }
    }
}
