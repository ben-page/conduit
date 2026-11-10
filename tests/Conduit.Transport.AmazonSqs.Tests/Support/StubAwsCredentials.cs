using Amazon.Runtime;

namespace Conduit.Transport.AmazonSqs.Tests.Support;

/// <summary>
/// Credentials a test hands to the transport's options and then looks for, by reference, on the clients the
/// transport builds. Nothing ever signs a request with them.
/// </summary>
internal sealed class StubAwsCredentials : AWSCredentials
{
    public override ImmutableCredentials GetCredentials() => new("stub-access-key", "stub-secret-key", token: null);
}
