using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class UseSignalRShould
{
    // A hub mapped behind RequireAuthorization needs a credential on the connection: ConfigureConnection reaches the
    // HubConnection's HTTP options, here its access token provider.
    [TestMethod]
    public async Task PassConnectionOptionsThrough()
    {
        // Arrange
        await using var host = TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<RecordedHubContexts>();
                builder.Services.AddSingleton<RecordedAuthorizationHeaders>();
                builder.Services.AddSignalR();
            },
            app =>
            {
                app.Use(async (context, next) =>
                {
                    var authorization = context.Request.Headers.Authorization.ToString();
                    if (authorization.Length > 0)
                    {
                        context.RequestServices.GetRequiredService<RecordedAuthorizationHeaders>().Add(authorization);
                    }

                    await next(context);
                });
                app.MapHub<ContextRecordingHub>("/recording");
            });
        var services = new ServiceCollection();
        new ConduitBuilder(services).AddEndpoint("inventory", endpoint => endpoint.UseSignalR(
            new Uri($"{host.Address}/recording"),
            options => options.ConfigureConnection = connection =>
                connection.AccessTokenProvider = () => Task.FromResult<string?>("token-1")));
        await using var serviceProvider = services.BuildServiceProvider();
        var transport = serviceProvider.GetRequiredKeyedService<SignalRTransport>("inventory");
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));

        // Act
        await transport.SendRequestAsync(envelope, CancellationToken.None);

        // Assert
        host.GetRequiredService<RecordedAuthorizationHeaders>().Received.Should().Contain("Bearer token-1");
    }
}
