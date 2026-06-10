using AwesomeAssertions;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Audit.Tests.Support;
using Conduit.Generated;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Audit.Tests.Integration;

/// <summary>
/// Proves the audit store's Singleton → Scoped lifetime change did not introduce a captive
/// dependency. The store is Scoped (it depends on the Scoped <see cref="Conduit.Persistence.IConduitConnectionFactory"/>),
/// so any singleton that injected it directly would throw
/// "Cannot consume scoped service ... from singleton" under scope validation. The purge service and
/// the dispatch path both resolve the store inside a per-operation scope; these tests build the host
/// with <c>ValidateScopes = true</c> and <c>ValidateOnBuild = true</c> and exercise both paths.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AuditScopeValidationShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task BuildAndStartAuditHostUnderScopeValidation()
    {
        // Arrange
        var connectionString = SharedInMemoryConnectionString(out var keepAlive);
        await keepAlive.OpenAsync();

        // Act — building with ValidateOnBuild + starting (which runs AuditPurgeService and
        // AuditTableInitializer) throws if any singleton captures the Scoped IAuditStore.
        var build = async () =>
        {
            var host = await StartValidatedHostAsync(connectionString);
            await DisposeHostAsync(host);
        };

        // Assert
        await build.Should().NotThrowAsync(
            "the purge service and table initializer must resolve the Scoped store from a scope, " +
            "never inject it into a singleton");

        keepAlive.Dispose();
    }

    [TestMethod]
    public async Task PurgeAndDispatchResolveScopedStoreFromAScope()
    {
        // Arrange
        var connectionString = SharedInMemoryConnectionString(out var keepAlive);
        await keepAlive.OpenAsync();

        var host = await StartValidatedHostAsync(connectionString);
        try
        {
            // Act / Assert — the purge service is a singleton BackgroundService; resolving the Scoped
            // store directly from the root provider would throw, but resolving it inside a scope (as
            // the base PeriodicCleanupServiceBase does each tick) must succeed.
            using (var scope = host.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IAuditStore>();
                var purge = async () => await store.PurgeExpiredAsync(DateTime.UtcNow, CancellationToken.None);
                await purge.Should().NotThrowAsync();
            }

            // The dispatch path publishes AuditBatch -> LocalAuditListener -> IAuditStore inside the
            // publish scope created by AuditBatchPublisher / the mediator. Dispatch a command and
            // confirm the audit record lands, proving the scoped store is reachable from that path.
            var mediator = host.Services.GetRequiredService<IMediator>();
            var capture = host.Services.GetRequiredService<MessageIdCapture>();

            var result = await mediator.ExecuteAsync(new AuditTestCommand("scope-check"));
            result.Should().Be("scope-check");

            var messageId = capture.MessageId;
            messageId.Should().NotBeNullOrEmpty();

            DispatchAuditRecord? record = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = host.Services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAuditStore>();
                record = await store.GetByMessageIdAsync(messageId!, CancellationToken.None);
                if (record is not null)
                {
                    break;
                }

                await Task.Delay(100);
            }

            record.Should().NotBeNull("the scoped store must be reachable from the dispatch publish scope");
        }
        finally
        {
            await DisposeHostAsync(host);
            keepAlive.Dispose();
        }
    }

    private static async Task DisposeHostAsync(IHost host)
    {
        await host.StopAsync();
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            host.Dispose();
        }
    }

    private static async Task<IHost> StartValidatedHostAsync(string connectionString)
    {
        // Host.CreateApplicationBuilder enables ValidateScopes + ValidateOnBuild automatically when the
        // environment is Development. That is the crux of this stage: the build/start fails if any
        // singleton captures the Scoped IAuditStore.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });

        builder.Services.AddSingleton<MessageIdCapture>();
        builder.Services.AddConduit(conduit =>
        {
            conduit.UseSqlite(connectionString);
            conduit.AddAudit(audit =>
            {
                audit.BatchSize = 1;
                audit.ServiceName = "ScopeValidationService";
            });
            conduit.AddAuditListener(_ => { });
        });

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    // A shared-cache in-memory SQLite database kept alive by a long-lived connection for the test
    // duration, so the per-call connections the store opens all see the same schema and rows.
    private static string SharedInMemoryConnectionString(out Microsoft.Data.Sqlite.SqliteConnection keepAlive)
    {
        var connectionString =
            $"Data Source=file:audit-scope-{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        return connectionString;
    }
}
