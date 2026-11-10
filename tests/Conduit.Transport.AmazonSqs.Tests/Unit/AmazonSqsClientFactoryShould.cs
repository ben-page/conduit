using Amazon;
using Amazon.Runtime;
using AwesomeAssertions;
using Conduit.Transport.AmazonSqs.Tests.Support;

namespace Conduit.Transport.AmazonSqs.Tests.Unit;

[TestClass]
public class AmazonSqsClientFactoryShould
{
    [TestMethod]
    public void UseConfiguredCredentialsAgainstAws()
    {
        // Arrange — credentials and a region, no custom endpoint
        var credentials = new StubAwsCredentials();
        var options = new AmazonSqsOptions { Region = RegionEndpoint.EUWest1, Credentials = credentials };

        // Act
        var (snsClient, sqsClient) = AmazonSqsClientFactory.Create(options);
        using var sns = snsClient;
        using var sqs = sqsClient;

        // Assert — both clients sign with the configured credentials, in the configured region
        sns.Config.DefaultAWSCredentials.Should().BeSameAs(credentials);
        sqs.Config.DefaultAWSCredentials.Should().BeSameAs(credentials);
        sns.Config.RegionEndpoint.Should().Be(RegionEndpoint.EUWest1);
        sqs.Config.RegionEndpoint.Should().Be(RegionEndpoint.EUWest1);
    }

    [TestMethod]
    public void UseConfiguredCredentialsWithACustomEndpoint()
    {
        // Arrange
        var credentials = new StubAwsCredentials();
        var options = new AmazonSqsOptions { ServiceUri = new Uri("http://localhost:4566"), Credentials = credentials };

        // Act
        var (snsClient, sqsClient) = AmazonSqsClientFactory.Create(options);
        using var sns = snsClient;
        using var sqs = sqsClient;

        // Assert — the endpoint is overridden, the credentials are still the configured ones
        sns.Config.DefaultAWSCredentials.Should().BeSameAs(credentials);
        sqs.Config.DefaultAWSCredentials.Should().BeSameAs(credentials);
        sns.Config.ServiceURL.Should().Be("http://localhost:4566/");
        sqs.Config.ServiceURL.Should().Be("http://localhost:4566/");
    }

    [TestMethod]
    public void UseAPlaceholderPairForACustomEndpointWithoutCredentials()
    {
        // Arrange
        var options = new AmazonSqsOptions { ServiceUri = new Uri("http://localhost:4566") };

        // Act
        var (snsClient, sqsClient) = AmazonSqsClientFactory.Create(options);
        using var sns = snsClient;
        using var sqs = sqsClient;

        // Assert — LocalStack validates nothing, but the SDK needs some credential source
        sns.Config.DefaultAWSCredentials.Should().BeOfType<BasicAWSCredentials>()
            .Which.GetCredentials().AccessKey.Should().Be("test");
        sqs.Config.DefaultAWSCredentials.Should().BeOfType<BasicAWSCredentials>()
            .Which.GetCredentials().AccessKey.Should().Be("test");
    }

    [TestMethod]
    public void PutNoCredentialsOnTheClientsAgainstAwsWithoutConfiguredOnes()
    {
        // Arrange
        var options = new AmazonSqsOptions { Region = RegionEndpoint.EUWest1 };

        // Act
        var (snsClient, sqsClient) = AmazonSqsClientFactory.Create(options);
        using var sns = snsClient;
        using var sqs = sqsClient;

        // Assert — neither configured nor placeholder credentials reach the clients, so the SDK resolves its own
        // through the default chain
        sns.Config.DefaultAWSCredentials.Should().BeNull();
        sqs.Config.DefaultAWSCredentials.Should().BeNull();
    }
}
