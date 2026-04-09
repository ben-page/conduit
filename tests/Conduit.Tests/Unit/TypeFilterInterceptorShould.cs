using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Middleware;
using Microsoft.Extensions.Options;

namespace Conduit.Tests.Unit;

[TestClass]
public class TypeFilterInterceptorShould
{
    private static TypeFilterInterceptor CreateInterceptor(params string[] allowedTypes)
    {
        var options = new TypeFilterOptions();
        foreach (var type in allowedTypes)
        {
            options.AllowedTypes.Add(type);
        }

        return new TypeFilterInterceptor(Options.Create(options));
    }

    [TestMethod]
    public async Task AllowPermittedType()
    {
        // Arrange
        var interceptor = CreateInterceptor("CreateOrder");

        // Act
        var result = await interceptor.ShouldProcessAsync("CreateOrder", CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public async Task RejectForbiddenType()
    {
        // Arrange
        var interceptor = CreateInterceptor("CreateOrder");

        // Act
        var action = () => interceptor.ShouldProcessAsync("DeleteDatabase", CancellationToken.None);

        // Assert
        await action.Should().ThrowAsync<Conduit.Exceptions.UnauthorizedException>()
            .WithMessage("*DeleteDatabase*");
    }
}
