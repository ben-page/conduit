using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.Messages;
using Conduit.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Tests.Unit;

[TestClass]
public class ScheduleBuilderShould
{
    [TestMethod]
    public void RejectAFireAndForgetBody()
    {
        // Arrange — the generated dispatch enqueues a [FireAndForget] handler and returns at once, so the
        // run would be recorded Completed before its handler had even started
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new FireAndForgetJob());

        // Act
        var act = () => new ConduitBuilder(new ServiceCollection()).AddJobs(Configure);

        // Assert — refused at declaration, naming the command and the reason
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'warm'*{typeof(FireAndForgetJob).FullName}*runs inline*");
    }

    [TestMethod]
    public void RejectAFireAndForgetBodyItBuildsBehindAWiderType()
    {
        // Arrange — declared as ICommand, so only the command the factory builds can tell
        var services = new ServiceCollection();
        new ConduitBuilder(services).AddJobs(
            jobs => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run<ICommand>(() => new FireAndForgetJob()));
        using var serviceProvider = services.BuildServiceProvider();
        var schedule = serviceProvider.GetRequiredService<ScheduleRegistry>().Schedules.Should().ContainSingle().Subject;

        // Act — what the durable scheduler's BuildRun, trigger-now and the in-memory scheduler all call
        var act = () => schedule.CreateCommand();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'warm'*{typeof(FireAndForgetJob).FullName}*runs inline*");
    }

    [TestMethod]
    public void RejectAFireAndForgetBodyOnAnEventTrigger()
    {
        // Arrange — event triggers need an engine
        var builder = new ConduitBuilder(new ServiceCollection());
        builder.UseSqlite("Data Source=:memory:");

        // Act
        var act = () => builder.AddJobs(
            jobs => jobs.On<ThingHappened>("notify").Run(_ => new FireAndForgetJob()));

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'notify'*{typeof(FireAndForgetJob).FullName}*runs inline*");
    }
}
