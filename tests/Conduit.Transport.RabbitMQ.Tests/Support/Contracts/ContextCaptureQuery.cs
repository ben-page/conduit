using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record ContextCaptureQuery(string Input) : IQuery<TestQueryResult>;
