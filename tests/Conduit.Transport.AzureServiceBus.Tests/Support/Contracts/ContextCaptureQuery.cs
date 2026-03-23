using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record ContextCaptureQuery(string Input) : IQuery<TestQueryResult>;
