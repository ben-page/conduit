using AwesomeAssertions;
using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.RabbitMQ.Tests.Unit;

[TestClass]
public class RabbitMqOptionsShould
{
    private const string ConnectionString = "amqp://guest:guest@localhost:5672/";

    private static ConduitBuilder CreateBuilder()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ConduitOptions());

        return new ConduitBuilder(services);
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(-1, false)]
    [DataRow(65_536, false)]
    [DataRow(0, true)]
    [DataRow(65_536, true)]
    public void RejectConcurrencyOutsideUShort(int concurrency, bool onAPool)
    {
        // Arrange — one concurrency is both a channel's prefetch count and its consumer dispatch
        // concurrency, and both are an AMQP short: a value outside 1..65535 would be silently cast
        var builder = CreateBuilder();

        // Act
        var act = () => builder.AddEndpoint("orders", endpoint => endpoint.UseRabbitMQ(ConnectionString, options =>
        {
            if (onAPool)
            {
                options.AddPool("bulk", pool => pool.Concurrency = concurrency);
            }
            else
            {
                options.DefaultConcurrency = concurrency;
            }
        }));

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage(onAPool ? "*Concurrency*'bulk'*1*65535*" : "*DefaultConcurrency*1*65535*")
            .Which.ActualValue.Should().Be(concurrency);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(65_535)]
    public void AcceptConcurrencyAtTheUShortBounds(int concurrency)
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var act = () => builder.AddEndpoint("orders", endpoint => endpoint.UseRabbitMQ(ConnectionString, options =>
        {
            options.DefaultConcurrency = concurrency;
            options.AddPool("bulk", pool => pool.Concurrency = concurrency);
        }));

        // Assert
        act.Should().NotThrow();
    }
}
