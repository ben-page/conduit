using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record TestQuery(string Input) : IQuery<TestQueryResult>;
