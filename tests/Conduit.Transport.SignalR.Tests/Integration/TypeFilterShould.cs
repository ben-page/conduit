using System.Text.Json;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

/// <summary>
/// The type filter gates every inbound message, events included, and the publisher hears the refusal: the hub's
/// <c>Publish</c> fails the invocation with the refusal's message.
/// </summary>
[TestClass]
public class TypeFilterShould
{
    [TestMethod]
    public async Task RefuseAnUnlistedEventAndAnswerTheCaller()
    {
        // Arrange -- the filter lists a command, not the event
        await using var host = TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                    conduit.AddTypeFilter(filter => filter.AllowType(typeof(CreateItemCommand).FullName!)));
                builder.Services.AddConduitSignalR();
            },
            app => app.MapHub<ConduitHub>("/conduit"));
        await using var connection = new HubConnectionBuilder().WithUrl($"{host.Address}/conduit").Build();
        await connection.StartAsync();
        var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid(), Name = "Unlisted" });
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;

        // Act
        var publish = () => connection.InvokeAsync(
            "Publish", typeof(ItemCreatedEvent).FullName, payload, noContext, noHeaders);

        // Assert -- the invocation fails with the refusal's message, and no listener ran
        await publish.Should().ThrowExactlyAsync<HubException>()
            .WithMessage($"*Type '{typeof(ItemCreatedEvent).FullName}' is not allowed by the type filter.*");
        host.GetRequiredService<EventSpy<ItemCreatedEvent>>().Count.Should().Be(0);
    }
}
