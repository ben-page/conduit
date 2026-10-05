using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Conduit.Tests.Support;

/// <summary>An <see cref="ILogger{T}"/> that records every entry, for asserting on what a component logged.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, EventId EventId, string Message)> entries = new();

    public IReadOnlyList<(LogLevel Level, EventId EventId, string Message)> Entries => [.. this.entries];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        this.entries.Enqueue((logLevel, eventId, formatter(state, exception)));
    }
}
