using System.Collections.Generic;

namespace Conduit.SourceGen;

/// <summary>
/// The result of scanning a compilation for command/query handlers and event listeners,
/// including the message types whose only handler is inaccessible to the generating assembly.
/// </summary>
public sealed class HandlerScanResult
{
    /// <summary>Initializes a new instance of the <see cref="HandlerScanResult"/> class.</summary>
    /// <param name="handlers">The discovered accessible command/query handlers.</param>
    /// <param name="listenerGroups">Event full type name to the listener implementation type names.</param>
    /// <param name="inaccessibleHandlerMessageTypes">Message types whose handler is not accessible.</param>
    public HandlerScanResult(
        IReadOnlyList<HandlerInfo> handlers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> listenerGroups,
        IReadOnlyList<string> inaccessibleHandlerMessageTypes)
    {
        this.Handlers = handlers;
        this.ListenerGroups = listenerGroups;
        this.InaccessibleHandlerMessageTypes = inaccessibleHandlerMessageTypes;
    }

    /// <summary>The discovered accessible command/query handlers.</summary>
    public IReadOnlyList<HandlerInfo> Handlers { get; }

    /// <summary>Event full type name to the listener implementation type names.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ListenerGroups { get; }

    /// <summary>Message types whose only handler is not accessible to the generating assembly.</summary>
    public IReadOnlyList<string> InaccessibleHandlerMessageTypes { get; }
}
