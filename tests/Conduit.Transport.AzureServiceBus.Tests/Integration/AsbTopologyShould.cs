using AwesomeAssertions;
using Azure.Messaging.ServiceBus.Administration;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

/// <summary>
/// Tests what startup does with a subscription that already exists and forwards elsewhere: one that
/// forwards to another of this application's own queues follows the application's configuration, and
/// one that forwards to another application's queue is a naming collision.
/// </summary>
[TestClass]
public class AsbTopologyShould : AsbTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.AzureServiceBus.Tests.Support.Contracts";
    private const string TestCommandTypeName = ContractsNamespace + ".TestCommand";
    private const string TestEventTypeName = ContractsNamespace + ".TestEvent";

    /// <summary>
    /// Creates a handler host whose pool <paramref name="poolName"/> handles <paramref name="pooledTypeNames"/>.
    /// </summary>
    private static async Task<TestHost> CreateHostWithPoolAsync(
        string applicationName,
        string topologyPrefix,
        string poolName,
        params string[] pooledTypeNames)
    {
        var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = TopologyMode.Test;
                conduit.TopologyPrefix = topologyPrefix;

                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseAzureServiceBus(ConnectionString, options =>
                    {
                        options.AdminConnectionString = AdminConnectionString;
                        options.AddPool(poolName, pool =>
                        {
                            foreach (var typeName in pooledTypeNames)
                            {
                                pool.Handle(typeName);
                            }
                        });
                    }));
            });
        });

        RegisterPrefixForCleanup(topologyPrefix);
        return host;
    }

    private static string ForwardTarget(SubscriptionProperties subscription) =>
        subscription.ForwardTo.Split('/')[^1];

    [TestMethod]
    public async Task UpdateASubscriptionWhenAPoolAssignmentChanges()
    {
        // Arrange -- the application's first release handles TestCommand and TestEvent on its default queue
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        string prefix;
        await using (var firstRelease = await CreateHostAsync(handlerName))
        {
            prefix = firstRelease.TopologyPrefix!;
        }

        // Act -- its next release moves both into a pool, so their subscriptions must forward elsewhere
        await using var secondRelease = await CreateHostWithPoolAsync(
            handlerName, prefix, "moved", TestCommandTypeName, TestEventTypeName);
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var result = await senderHost.GetRequiredService<IMediator>().ExecuteAsync(new TestCommand("moved"));

        // Assert -- the release started, the shared command subscription and the application's own event
        // subscription now forward to the pool's queue, and the command was handled through it
        var conduitOptions = secondRelease.GetRequiredService<ConduitOptions>();
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 260);
        var poolQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "moved", 260);
        var adminClient = new ServiceBusAdministrationClient(AdminConnectionString);

        SubscriptionProperties commandSubscription = await adminClient.GetSubscriptionAsync(
            topicName, TopologyNaming.CreateSharedResourceName(conduitOptions, TestCommandTypeName, 50));
        SubscriptionProperties eventSubscription = await adminClient.GetSubscriptionAsync(
            topicName, TopologyNaming.CreateAppResourceName(conduitOptions, TestEventTypeName, 50));

        ForwardTarget(commandSubscription).Should().BeEquivalentTo(poolQueueName);
        ForwardTarget(eventSubscription).Should().BeEquivalentTo(poolQueueName);
        result.Value.Should().Be("handled:moved");
    }

    [TestMethod]
    public async Task RefuseASubscriptionThatForwardsToAnotherApplication()
    {
        // Arrange -- one application handles the test project's commands
        var firstName = UniqueAppName("first");
        var secondName = UniqueAppName("second");

        await using var firstHost = await CreateHostAsync(firstName);
        var prefix = firstHost.TopologyPrefix!;

        // Act -- a second application that handles them too starts on the same topology
        var starting = CreateHostAsync(secondName, prefix);

        // Assert -- a command has one handling application, so the second one does not take its
        // subscription over: it refuses to start
        await starting.Awaiting(task => task).Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("*naming collision between applications*");
    }
}
