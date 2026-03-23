using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record ContextCaptureQuery(string Input) : IQuery<TestQueryResult>;
