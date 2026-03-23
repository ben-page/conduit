using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record ContextCaptureQuery(string Input) : IQuery<TestQueryResult>;
