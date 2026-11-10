using System.Collections.Concurrent;

namespace Conduit.TestUtils;

public sealed class SpyLog
{
    private readonly ConcurrentQueue<object> invocations = new();

    public void Record(object message) => this.invocations.Enqueue(message);

    public IReadOnlyList<object> Invocations => [.. this.invocations];

    public int Count => this.invocations.Count;

    /// <summary>
    /// Waits until one of <paramref name="spies"/> records a <typeparamref name="T"/> and returns that
    /// spy's index: which of several competing instances took a message.
    /// </summary>
    public static async Task<int> WaitForFirstAsync<T>(TimeSpan timeout, params SpyLog[] spies)
    {
        ArgumentNullException.ThrowIfNull(spies);

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            for (var index = 0; index < spies.Length; index++)
            {
                if (spies[index].Invocations.OfType<T>().Any())
                {
                    return index;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No spy recorded a {typeof(T).Name} within {timeout}");
            }

            await Task.Delay(50);
        }
    }

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
