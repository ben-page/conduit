using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

[LocalOnly]
public record LocalOnlyTestEvent(string Value) : IEvent;
