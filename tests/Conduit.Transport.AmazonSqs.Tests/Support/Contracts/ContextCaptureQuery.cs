using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record ContextCaptureQuery(string Input) : IQuery<TestQueryResult>;
