using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record ContextCaptureQuery(string Input) : IQuery<TestQueryResult>;
