using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record ThrowingQuery(string Input) : IQuery<TestQueryResult>;
