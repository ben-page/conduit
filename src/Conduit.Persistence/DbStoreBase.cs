using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Conduit.Persistence;

/// <summary>
/// Base class for ADO.NET persistence stores. Provides the disposal-correct
/// connection-per-method pattern (<c>await using var connection = await
/// factory.CreateConnectionAsync(...)</c>) and small command helpers, mirroring the patterns in the
/// durability and audit stores. A static <see cref="ExecuteOnConnectionAsync"/> overload supports the
/// outbox <c>SaveAsync</c> path, which must write through a caller-supplied transaction's own
/// connection rather than opening its own.
/// </summary>
// CA2100: All SQL passed through these helpers comes from the persistence dialect or module-owned
// schema descriptors — trusted internal infrastructure, never user-supplied input.
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "All SQL originates from ISqlDialect or module-owned schema descriptors, which are trusted infrastructure, never user-supplied input.")]
public abstract class DbStoreBase
{
    /// <summary>Initializes a new instance of <see cref="DbStoreBase"/>.</summary>
    /// <param name="connectionFactory">The connection factory used to open per-method connections.</param>
    protected DbStoreBase(IConduitConnectionFactory connectionFactory)
    {
        this.ConnectionFactory = connectionFactory;
    }

    /// <summary>The connection factory backing this store.</summary>
    protected IConduitConnectionFactory ConnectionFactory { get; }

    /// <summary>
    /// Opens a connection (owned and disposed here), executes a non-query command, and returns the
    /// number of rows affected. <paramref name="configureParameters"/> binds any parameters.
    /// </summary>
    protected async Task<int> ExecuteNonQueryAsync(
        string commandText,
        Action<DbCommand>? configureParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        configureParameters?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Opens a connection (owned and disposed here), executes a scalar command, and returns the
    /// first column of the first row (or <see langword="null"/>).
    /// </summary>
    protected async Task<object?> ExecuteScalarAsync(
        string commandText,
        Action<DbCommand>? configureParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        configureParameters?.Invoke(command);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// Opens a connection (owned and disposed here), executes a reader command, and projects every
    /// row through <paramref name="readRow"/> into a list.
    /// </summary>
    protected async Task<List<T>> ExecuteReaderAsync<T>(
        string commandText,
        Action<DbCommand>? configureParameters,
        Func<DbDataReader, T> readRow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readRow);

        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        configureParameters?.Invoke(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(readRow(reader));
        }

        return results;
    }

    /// <summary>
    /// Runs the statements that create a table and its indexes when they are absent
    /// (<see cref="ISqlDialect.BuildCreateTableIfNotExistsSql(string, string, IReadOnlyList{TableIndex})"/>), in
    /// order, each on a connection of its own. Replicas starting together on an empty database can all find an object
    /// absent and all create it, and the engine refuses every create but the first
    /// (<see cref="ISqlDialect.IsAlreadyExists"/>). Such a statement has nothing left to do, so it is passed over and
    /// the statements after it still run: every replica starts, and each still ensures the indexes of a table another
    /// replica created. Any other failure is thrown.
    /// </summary>
    /// <param name="dialect">The engine's dialect, which recognizes its already-exists errors.</param>
    /// <param name="statements">The create-if-absent statements, in the order they must run.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    protected async Task EnsureCreatedAsync(
        ISqlDialect dialect,
        IEnumerable<string> statements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(statements);

        foreach (var statement in statements)
        {
            try
            {
                await this.ExecuteNonQueryAsync(statement, configureParameters: null, cancellationToken);
            }
            catch (DbException exception) when (dialect.IsAlreadyExists(exception))
            {
                // Another replica created this table or index between this statement's check and its create.
            }
        }
    }

    /// <summary>
    /// Executes a non-query command on an existing connection, enlisted in the supplied transaction.
    /// Used by the outbox <c>SaveAsync</c> path, which writes through the caller-owned transaction's
    /// connection and does not open or dispose a connection of its own.
    /// </summary>
    public static async Task ExecuteOnConnectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        Action<DbCommand>? configureParameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        configureParameters?.Invoke(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
