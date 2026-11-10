using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>A fire-and-forget command: routed, it goes out with <c>SendEventAsync</c> and no reply is awaited.</summary>
[FireAndForget]
public record NoteItemCommand(Guid Id, string Name) : ICommand;
