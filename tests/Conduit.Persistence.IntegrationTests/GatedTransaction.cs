using System.Data;
using System.Data.Common;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>A pass-through transaction; <see cref="GatedCommand"/> unwraps it to enlist the real one.</summary>
internal sealed class GatedTransaction : DbTransaction
{
    private readonly GatedConnection connection;

    public GatedTransaction(GatedConnection connection, DbTransaction inner)
    {
        this.connection = connection;
        this.Inner = inner;
    }

    /// <summary>The engine's own transaction.</summary>
    public DbTransaction Inner { get; }

    public override IsolationLevel IsolationLevel => this.Inner.IsolationLevel;

    protected override DbConnection DbConnection => this.connection;

    public override void Commit() => this.Inner.Commit();

    public override void Rollback() => this.Inner.Rollback();

    public override Task CommitAsync(CancellationToken cancellationToken = default)
        => this.Inner.CommitAsync(cancellationToken);

    public override Task RollbackAsync(CancellationToken cancellationToken = default)
        => this.Inner.RollbackAsync(cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        await this.Inner.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.Inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
