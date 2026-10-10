using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>A pass-through connection whose commands report to a <see cref="GatedConnectionFactory"/>.</summary>
internal sealed class GatedConnection : DbConnection
{
    private readonly DbConnection inner;
    private readonly GatedConnectionFactory gate;

    public GatedConnection(DbConnection inner, GatedConnectionFactory gate)
    {
        this.inner = inner;
        this.gate = gate;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => this.inner.ConnectionString;
        set => this.inner.ConnectionString = value;
    }

    public override string Database => this.inner.Database;

    public override string DataSource => this.inner.DataSource;

    public override string ServerVersion => this.inner.ServerVersion;

    public override ConnectionState State => this.inner.State;

    public override void ChangeDatabase(string databaseName) => this.inner.ChangeDatabase(databaseName);

    public override void Close() => this.inner.Close();

    public override void Open() => this.inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => this.inner.OpenAsync(cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        await this.inner.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => new GatedTransaction(this, this.inner.BeginTransaction(isolationLevel));

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel, CancellationToken cancellationToken)
        => new GatedTransaction(this, await this.inner.BeginTransactionAsync(isolationLevel, cancellationToken));

    protected override DbCommand CreateDbCommand() => new GatedCommand(this.inner.CreateCommand(), this, this.gate);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
