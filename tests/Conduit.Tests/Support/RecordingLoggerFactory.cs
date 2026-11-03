using Microsoft.Extensions.Logging;

namespace Conduit.Tests.Support;

/// <summary>An <see cref="ILoggerFactory"/> whose loggers all write to one <see cref="RecordingLogger{T}"/>, and which remembers the categories asked for.</summary>
public sealed class RecordingLoggerFactory : ILoggerFactory
{
    public RecordingLogger<object> Logger { get; } = new();

    public IReadOnlyList<string> Categories => [.. this.categories];

    private readonly List<string> categories = [];

    public ILogger CreateLogger(string categoryName)
    {
        this.categories.Add(categoryName);
        return this.Logger;
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
