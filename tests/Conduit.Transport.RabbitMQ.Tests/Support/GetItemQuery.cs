using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public record GetItemQuery(Guid Id) : IQuery<string>;
