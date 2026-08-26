using Conduit.Messages;

namespace Conduit.Jobs.Tests.Support;

/// <summary>An event whose coalesce key is numeric, pinning invariant-culture formatting.</summary>
public sealed record NumericPunch(decimal Sequence) : IEvent;
