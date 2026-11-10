using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// A fire-and-forget command: the generated dispatch hands its handler to the background dispatcher and
/// returns at once, so it cannot be a job body.
/// </summary>
[FireAndForget]
public sealed record FireAndForgetJob : ICommand;
