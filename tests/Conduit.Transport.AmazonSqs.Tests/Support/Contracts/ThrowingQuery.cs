using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record ThrowingQuery(string Input) : IQuery<TestQueryResult>;
