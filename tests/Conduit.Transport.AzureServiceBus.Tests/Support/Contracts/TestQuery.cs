using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record TestQuery(string Input) : IQuery<TestQueryResult>;
