using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AmazonSqs.Tests.Integration;

/// <summary>
/// Tests competing consumer behavior over Amazon SQS: multiple instances with the same
/// ApplicationName share a queue, so each message is delivered to exactly one instance.
/// For events, each unique ApplicationName gets its own SNS subscription forwarding to
/// its own queue, so every app gets a copy but competing instances within the same app
/// still deduplicate.
/// </summary>
[TestClass]
public class SqsCompetingInstanceShould : SqsTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.AmazonSqs.Tests.Support.Contracts";

    [TestMethod]
    [DataRow(1)]
    [DataRow(20)]
    public async Task DeliverCommandToExactlyOneInstance(int messageCount)
    {
        // Arrange
        var sharedHandlerName = UniqueAppName("shared");
        var senderName = UniqueAppName("sender");

        // 3 handler instances with the SAME ApplicationName -- competing consumers
        await using var handler1 = await CreateHostAsync(sharedHandlerName);
        var prefix = handler1.TopologyPrefix!;
        await using var handler2 = await CreateHostAsync(sharedHandlerName, prefix);
        await using var handler3 = await CreateHostAsync(sharedHandlerName, prefix);

        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var spy1 = handler1.GetRequiredService<SpyLog>();
        var spy2 = handler2.GetRequiredService<SpyLog>();
        var spy3 = handler3.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        for (var i = 0; i < messageCount; i++)
        {
            await mediator.ExecuteAsync(new TestCommand($"msg-{i}"));
        }

        // Assert -- total across all instances equals exactly messageCount (each handled once)
        var totalSpy = spy1.Count + spy2.Count + spy3.Count;
        totalSpy.Should().Be(messageCount,
            $"Expected exactly {messageCount} total invocations across 3 competing instances, got {totalSpy}");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(20)]
    public async Task DeliverQueryToExactlyOneInstance(int messageCount)
    {
        // Arrange
        var sharedHandlerName = UniqueAppName("shared");
        var senderName = UniqueAppName("sender");

        // 3 handler instances with the SAME ApplicationName -- competing consumers
        await using var handler1 = await CreateHostAsync(sharedHandlerName);
        var prefix = handler1.TopologyPrefix!;
        await using var handler2 = await CreateHostAsync(sharedHandlerName, prefix);
        await using var handler3 = await CreateHostAsync(sharedHandlerName, prefix);

        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var spy1 = handler1.GetRequiredService<SpyLog>();
        var spy2 = handler2.GetRequiredService<SpyLog>();
        var spy3 = handler3.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        for (var i = 0; i < messageCount; i++)
        {
            var result = await mediator.FetchAsync(new TestQuery($"query-{i}"));
            result.Value.Should().StartWith("result:");
        }

        // Assert -- total across all instances equals exactly messageCount
        var totalSpy = spy1.Count + spy2.Count + spy3.Count;
        totalSpy.Should().Be(messageCount,
            $"Expected exactly {messageCount} total invocations across 3 competing instances, got {totalSpy}");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(20)]
    public async Task DeliverEventToExactlyOneInstancePerApp(int messageCount)
    {
        // Arrange
        var appNameB = UniqueAppName("app-b");
        var appNameC = UniqueAppName("app-c");
        var publisherName = UniqueAppName("pub");

        // 2 instances of AppB (same name) + 2 instances of AppC (same name)
        await using var hostB1 = await CreateHostAsync(appNameB);
        var prefix = hostB1.TopologyPrefix!;
        await using var hostB2 = await CreateHostAsync(appNameB, prefix);
        await using var hostC1 = await CreateHostAsync(appNameC, prefix);
        await using var hostC2 = await CreateHostAsync(appNameC, prefix);

        await using var publisherHost = await CreateHostAsync(publisherName, prefix);

        var spyB1 = hostB1.GetRequiredService<SpyLog>();
        var spyB2 = hostB2.GetRequiredService<SpyLog>();
        var spyC1 = hostC1.GetRequiredService<SpyLog>();
        var spyC2 = hostC2.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        // Act
        for (var i = 0; i < messageCount; i++)
        {
            await mediator.PublishAsync(new TestEvent($"event-{i}"));
        }

        // Assert -- each app (B and C) received exactly messageCount total across its instances
        var totalB = spyB1.Count + spyB2.Count;
        var totalC = spyC1.Count + spyC2.Count;

        // Wait for delivery with some settling time
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while ((totalB < messageCount || totalC < messageCount) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            totalB = spyB1.Count + spyB2.Count;
            totalC = spyC1.Count + spyC2.Count;
        }

        totalB.Should().Be(messageCount,
            $"AppB should have received {messageCount} events total across 2 instances, got {totalB}");
        totalC.Should().Be(messageCount,
            $"AppC should have received {messageCount} events total across 2 instances, got {totalC}");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task DeliverRepliesToTheInstanceThatAsked()
    {
        // Arrange -- two instances of one sending application, each polling for replies; with one reply
        // queue for the application, either could take the other's reply and the asker would time out
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var firstSender = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker", ShortResponseTimeout);
        await using var secondSender = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker", ShortResponseTimeout);

        using var sqsClient = CreateSqsClient();
        var replyQueuePrefix = TopologyNaming.CreateAppResourceName(
            firstSender.GetRequiredService<ConduitOptions>(), "replies", 80) + "-";
        var firstMediator = firstSender.GetRequiredService<IMediator>();
        var secondMediator = secondSender.GetRequiredService<IMediator>();

        // Act -- each instance asks ten questions at once
        var firstQueries = Enumerable.Range(0, 10).Select(index => $"first-{index}").ToList();
        var secondQueries = Enumerable.Range(0, 10).Select(index => $"second-{index}").ToList();
        var firstReplies = Task.WhenAll(firstQueries.Select(input => firstMediator.FetchAsync(new TestQuery(input))));
        var secondReplies = Task.WhenAll(secondQueries.Select(input => secondMediator.FetchAsync(new TestQuery(input))));

        // Assert -- every question reached the instance that asked it, on a reply queue of its own
        (await firstReplies).Select(result => result.Value).Should()
            .BeEquivalentTo(firstQueries.Select(input => $"result:{input}"));
        (await secondReplies).Select(result => result.Value).Should()
            .BeEquivalentTo(secondQueries.Select(input => $"result:{input}"));
        (await ListQueuesAsync(sqsClient, replyQueuePrefix)).Should().HaveCount(2);

        // Stopping an instance deletes its reply queue: nothing is left behind for each restart
        await firstSender.DisposeAsync();
        await secondSender.DisposeAsync();
        (await ListQueuesAsync(sqsClient, replyQueuePrefix)).Should().BeEmpty();
    }

    // Replies lost to the other instance fail in seconds rather than a minute. The visibility timeout
    // may not be shorter than the response timeout.
    private static void ShortResponseTimeout(AmazonSqsOptions options)
    {
        options.ResponseTimeout = TimeSpan.FromSeconds(10);
        options.VisibilityTimeoutSeconds = 10;
    }

    private static async Task<List<string>> ListQueuesAsync(AmazonSQSClient sqsClient, string namePrefix)
    {
        var response = await sqsClient.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = namePrefix });
        return response.QueueUrls ?? [];
    }
}
