using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class AddDispatchRetryShould
{
    [TestMethod]
    public async Task Register_the_policy_the_options_and_the_runner_as_singletons()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        new ConduitBuilder(services).AddDispatchRetry<RecordingRetryPolicy>(options => options.MaxAttempts = 5);

        // Assert
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        provider.GetRequiredService<IDispatchRetryPolicy>().Should().BeOfType<RecordingRetryPolicy>()
            .And.BeSameAs(scope.ServiceProvider.GetRequiredService<IDispatchRetryPolicy>());
        provider.GetRequiredService<DispatchRetryOptions>().MaxAttempts.Should().Be(5);
        provider.GetRequiredService<DispatchRetryRunner>()
            .Should().BeSameAs(scope.ServiceProvider.GetRequiredService<DispatchRetryRunner>());
    }

    [TestMethod]
    public void Default_to_three_attempts_on_the_system_clock()
    {
        // Arrange
        var options = new DispatchRetryOptions();

        // Act
        var delays = options.Delays.ToList();

        // Assert
        options.MaxAttempts.Should().Be(3);
        options.TimeProvider.Should().BeSameAs(TimeProvider.System);
        delays.Should().Equal(
            new DispatchRetryDelay(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)),
            new DispatchRetryDelay(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300)));
    }

    [TestMethod]
    public void Throw_on_a_second_call()
    {
        // Arrange
        var builder = new ConduitBuilder(new ServiceCollection());
        builder.AddDispatchRetry<RecordingRetryPolicy>();

        // Act
        var act = () => builder.AddDispatchRetry<RecordingRetryPolicy>();

        // Assert
        act.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("AddDispatchRetry was already called; a host has one retry policy.");
    }

    [TestMethod]
    public void Throw_on_a_second_call_from_another_builder_over_the_same_services()
    {
        // Arrange
        var services = new ServiceCollection();
        new ConduitBuilder(services).AddDispatchRetry<RecordingRetryPolicy>();

        // Act
        var act = () => new ConduitBuilder(services).AddDispatchRetry<RecordingRetryPolicy>();

        // Assert
        act.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("AddDispatchRetry was already called; a host has one retry policy.");
    }

    [TestMethod]
    public void Throw_when_MaxAttempts_is_below_one()
    {
        // Act
        var act = () => new ConduitBuilder(new ServiceCollection())
            .AddDispatchRetry<RecordingRetryPolicy>(options => options.MaxAttempts = 0);

        // Assert
        act.Should().ThrowExactly<InvalidOperationException>().WithMessage("*MaxAttempts*");
    }

    [TestMethod]
    public void Throw_when_a_delays_min_exceeds_its_max()
    {
        // Act
        var act = () => new ConduitBuilder(new ServiceCollection())
            .AddDispatchRetry<RecordingRetryPolicy>(options =>
                options.Delays.Add(new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))));

        // Assert
        act.Should().ThrowExactly<InvalidOperationException>().WithMessage("*Delays*");
    }

    [TestMethod]
    public void Throw_when_a_delay_is_negative()
    {
        // Act
        var act = () => new ConduitBuilder(new ServiceCollection())
            .AddDispatchRetry<RecordingRetryPolicy>(options =>
                options.Delays.Add(new(TimeSpan.FromMilliseconds(-1), TimeSpan.FromSeconds(1))));

        // Assert
        act.Should().ThrowExactly<InvalidOperationException>().WithMessage("*Delays*");
    }

    [TestMethod]
    public void Throw_when_there_are_no_delays_for_a_retry()
    {
        // Act
        var act = () => new ConduitBuilder(new ServiceCollection())
            .AddDispatchRetry<RecordingRetryPolicy>(options => options.Delays.Clear());

        // Assert
        act.Should().ThrowExactly<InvalidOperationException>().WithMessage("*Delays*");
    }

    [TestMethod]
    public void Accept_no_delays_for_a_single_attempt()
    {
        // Act
        var act = () => new ConduitBuilder(new ServiceCollection())
            .AddDispatchRetry<RecordingRetryPolicy>(options =>
            {
                options.MaxAttempts = 1;
                options.Delays.Clear();
            });

        // Assert
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Throw_when_the_TimeProvider_is_null()
    {
        // Act
        var act = () => new ConduitBuilder(new ServiceCollection())
            .AddDispatchRetry<RecordingRetryPolicy>(options => options.TimeProvider = null!);

        // Assert
        act.Should().ThrowExactly<InvalidOperationException>().WithMessage("*TimeProvider*");
    }

    [TestMethod]
    public void Register_nothing_when_the_options_are_invalid()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new ConduitBuilder(services);

        // Act
        var act = () => builder.AddDispatchRetry<RecordingRetryPolicy>(options => options.MaxAttempts = 0);

        // Assert -- so a corrected call is not refused as a second one
        act.Should().Throw<InvalidOperationException>();
        services.Should().BeEmpty();
    }
}
