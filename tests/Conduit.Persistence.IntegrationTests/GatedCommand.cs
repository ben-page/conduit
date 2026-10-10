using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// A pass-through command that reports each execution to its <see cref="GatedConnectionFactory"/>
/// after it has run, so the gate can hold the call right there.
/// </summary>
internal sealed class GatedCommand : DbCommand
{
    private readonly DbCommand inner;
    private readonly GatedConnection connection;
    private readonly GatedConnectionFactory gate;
    private DbTransaction? transaction;

    public GatedCommand(DbCommand inner, GatedConnection connection, GatedConnectionFactory gate)
    {
        this.inner = inner;
        this.connection = connection;
        this.gate = gate;
    }

    [AllowNull]
    public override string CommandText
    {
        get => this.inner.CommandText;
#pragma warning disable CA2100 // Forwards the store's own SQL, set by the store; nothing here composes it.
        set => this.inner.CommandText = value;
#pragma warning restore CA2100
    }

    public override int CommandTimeout
    {
        get => this.inner.CommandTimeout;
        set => this.inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => this.inner.CommandType;
        set => this.inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => this.inner.DesignTimeVisible;
        set => this.inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => this.inner.UpdatedRowSource;
        set => this.inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => this.connection;
        set => throw new NotSupportedException("A gated command stays on the connection that created it.");
    }

    protected override DbParameterCollection DbParameterCollection => this.inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => this.transaction;
        set
        {
            this.transaction = value;
            this.inner.Transaction = value is GatedTransaction gated ? gated.Inner : value;
        }
    }

    public override void Cancel() => this.inner.Cancel();

    public override int ExecuteNonQuery() => this.inner.ExecuteNonQuery();

    public override object? ExecuteScalar() => this.inner.ExecuteScalar();

    public override void Prepare() => this.inner.Prepare();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var affected = await this.inner.ExecuteNonQueryAsync(cancellationToken);
        await this.gate.AfterCommandAsync(this.inner.CommandText);
        return affected;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var scalar = await this.inner.ExecuteScalarAsync(cancellationToken);
        await this.gate.AfterCommandAsync(this.inner.CommandText);
        return scalar;
    }

    protected override DbParameter CreateDbParameter() => this.inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => this.inner.ExecuteReader(behavior);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
        => this.inner.ExecuteReaderAsync(behavior, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
