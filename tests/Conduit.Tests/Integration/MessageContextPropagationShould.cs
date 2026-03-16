using AwesomeAssertions;
using Conduit.Dispatch;
using Conduit.Generated;
using Conduit.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class MessageContextPropagationShould
{
    [TestInitialize]
    public void Setup()
    {
        CapturedContext.Reset();
    }

    [TestMethod]
    public async Task SetContextDuringCommandDispatch()
    {
        // Arrange
        var mediator = CreateMediator();

        // Act
        await mediator.ExecuteAsync(new EchoCommand("test"));

        // Assert
        CapturedContext.Last.Should().NotBeNull();
        CapturedContext.Last!.MessageId.Should().NotBeNullOrEmpty();
        CapturedContext.Last.ConversationId.Should().Be(CapturedContext.Last.MessageId);
        CapturedContext.Last.CausationId.Should().BeNull();
    }

    [TestMethod]
    public async Task ReturnNullContextAfterDispatchCompletes()
    {
        // Arrange
        var mediator = CreateMediator();

        // Act
        await mediator.ExecuteAsync(new EchoCommand("test"));

        // Assert
        MessageContext.Current.Should().BeNull();
    }

    [TestMethod]
    public async Task SetContextDuringEventPublish()
    {
        // Arrange
        var mediator = CreateMediator();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "test"));

        // Assert
        CapturedContext.Last.Should().NotBeNull();
        CapturedContext.Last!.MessageId.Should().NotBeNullOrEmpty();
    }

    private static IMediator CreateMediator()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddConduit(conduit =>
        {
            conduit.AddPipeline(typeof(ContextCapturingBehavior<,>));
        });
        var app = builder.Build();
        return app.Services.CreateScope().ServiceProvider.GetRequiredService<IMediator>();
    }
}
