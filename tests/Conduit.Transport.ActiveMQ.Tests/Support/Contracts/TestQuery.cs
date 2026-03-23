using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record TestQuery(string Input) : IQuery<TestQueryResult>;
