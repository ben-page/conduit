using AwesomeAssertions;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Transport.SignalR;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.Http.Tests.Integration.FileUploads;

[TestClass]
public class StartupValidationShould
{
    [TestMethod]
    public void ThrowWhenCommandDeclaresTwoFileProperties()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();
        var app = builder.Build();

        // Act
        Action act = () => app.MapConduitPost<MultiFileCommand>("/uploads/multi");

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MultiFileCommand*2 file properties*First*Second*only one file property is supported*");
    }
}
