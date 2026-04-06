using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

[FireAndForget]
public record FireAndForgetEvent(string Value) : IEvent;
