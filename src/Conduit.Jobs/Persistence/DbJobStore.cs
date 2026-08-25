using System.Data.Common;
using System.Globalization;
using Conduit.Jobs.Configuration;
using Conduit.Persistence;
using Microsoft.Extensions.Options;

namespace Conduit.Jobs.Persistence;

/// <summary>
/// ADO.NET jobs store. Extends <see cref="DbStoreBase"/> for the disposal-correct
/// connection-per-method pattern, resolving the engine from DI via
/// <see cref="IConduitConnectionFactory"/> + <see cref="ISqlDialect"/>. Identifiers are module-owned
/// (see <see cref="JobTableSchema"/>); engine-specific idioms — the atomic claim, insert-or-ignore,
/// DDL type names — come from the dialect. Timestamps are written as round-trippable UTC ISO 8601
/// text and parsed back on read, matching the durability and audit stores, so no engine needs a
/// native datetime binding and lexical ordering matches chronological ordering.
/// </summary>
public sealed class DbJobStore : DbStoreBase, IJobStore
{
    private const string RunColumns =
        "Id, JobKey, ScheduleName, Occurrence, MessageType, Payload, RunAt, Status, ClaimedAt, " +
        "ClaimToken, AttemptCount, LastError, CancellationRequested, StartedAt";

    private static readonly string[] MaterializeColumns =
    [
        "JobKey", "ScheduleName", "Occurrence", "MessageType", "Payload", "RunAt", "Status",
        "AttemptCount", "CancellationRequested",
    ];

    private static readonly string[] InsertClaimedColumns =
    [
        "JobKey", "ScheduleName", "Occurrence", "MessageType", "Payload", "RunAt", "Status",
        "AttemptCount", "CancellationRequested", "ClaimedAt", "ClaimToken", "StartedAt",
    ];

    private static readonly string[] ReconcileScheduleColumns = ["Name", "IsPaused"];

    private readonly IOptions<JobsOptions> options;
    private readonly string createRunsTableSql;
    private readonly string createSchedulesTableSql;
    private readonly string claimDueSql;
    private readonly string materializeSql;
    private readonly string insertClaimedSql;
    private readonly string reconcileScheduleSql;

    /// <summary>Initializes a new instance of <see cref="DbJobStore"/>.</summary>
    /// <param name="connectionFactory">The connection factory used to open per-method connections.</param>
    /// <param name="dialect">The engine dialect supplying claim SQL and DDL type names.</param>
    /// <param name="options">Jobs tuning; the claim timeout decides when a claim has gone stale.</param>
    public DbJobStore(
        IConduitConnectionFactory connectionFactory,
        ISqlDialect dialect,
        IOptions<JobsOptions> options)
        : base(connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;

        this.createRunsTableSql = BuildCreateRunsTableSql(dialect);
        this.createSchedulesTableSql = BuildCreateSchedulesTableSql(dialect);
        this.claimDueSql = dialect.BuildClaimPendingSql(JobRunsTableSchema.Default);
        this.materializeSql = dialect.BuildInsertOrIgnoreSql(
            JobTableSchema.RunsTable, MaterializeColumns, "JobKey");
        this.insertClaimedSql = dialect.BuildInsertOrIgnoreSql(
            JobTableSchema.RunsTable, InsertClaimedColumns, "JobKey");
        this.reconcileScheduleSql = dialect.BuildInsertOrIgnoreSql(
            JobTableSchema.SchedulesTable, ReconcileScheduleColumns, "Name");
    }

    /// <inheritdoc/>
    public async Task EnsureTablesExistAsync(CancellationToken cancellationToken)
    {
        await this.ExecuteNonQueryAsync(this.createSchedulesTableSql, configureParameters: null, cancellationToken);
        await this.ExecuteNonQueryAsync(this.createRunsTableSql, configureParameters: null, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ReconcileSchedulesAsync(
        IReadOnlyList<string> scheduleNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scheduleNames);

        foreach (var name in scheduleNames)
        {
            await this.ExecuteNonQueryAsync(
                this.reconcileScheduleSql,
                command =>
                {
                    DbParameterHelper.Add(command, "@Name", name);
                    DbParameterHelper.Add(command, "@IsPaused", 0);
                },
                cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<JobScheduleRow?> GetScheduleAsync(string name, CancellationToken cancellationToken)
    {
        var rows = await this.ExecuteReaderAsync(
            $"SELECT Name, LastCompletedOccurrence, IsPaused, PausedAt FROM {JobTableSchema.SchedulesTable} WHERE Name = @Name",
            command => DbParameterHelper.Add(command, "@Name", name),
            ReadScheduleRow,
            cancellationToken);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobScheduleRow>> GetSchedulesAsync(CancellationToken cancellationToken)
        => await this.ExecuteReaderAsync(
            $"SELECT Name, LastCompletedOccurrence, IsPaused, PausedAt FROM {JobTableSchema.SchedulesTable} ORDER BY Name",
            configureParameters: null,
            ReadScheduleRow,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> TryMaterializeAsync(JobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var inserted = await this.ExecuteNonQueryAsync(
            this.materializeSql,
            command => BindMaterializeParameters(command, run),
            cancellationToken);

        return inserted > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobRun>> ClaimDueAsync(
        string claimToken, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        // Atomically move due rows — pending, or holding a claim that has gone stale because their
        // host stopped heartbeating — to Claiming under this worker's token, then read back exactly
        // the rows tagged with it. The UPDATE is the exclusivity primitive: two workers racing for
        // one occurrence can never both tag it.
        var claimed = await this.ExecuteNonQueryAsync(
            this.claimDueSql,
            command =>
            {
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
                DbParameterHelper.Add(command, "@ClaimedAt", FormatTimestamp(now));
                DbParameterHelper.Add(command, "@GraceCutoff", FormatTimestamp(now));
                DbParameterHelper.Add(command, "@ClaimCutoff", FormatTimestamp(now - this.options.Value.ClaimTimeout));
                DbParameterHelper.Add(command, "@BatchSize", batchSize);
                DbParameterHelper.Add(command, "@PendingStatus", (int)JobRunStatus.Pending);
                DbParameterHelper.Add(command, "@ClaimingStatus", (int)JobRunStatus.Claiming);
            },
            cancellationToken);

        if (claimed == 0)
        {
            return [];
        }

        // A re-claim starts a fresh attempt, so MaxRuntime is measured from now, not from whenever
        // the host that died began.
        await this.ExecuteNonQueryAsync(
            $"UPDATE {JobTableSchema.RunsTable} SET StartedAt = @StartedAt WHERE ClaimToken = @ClaimToken",
            command =>
            {
                DbParameterHelper.Add(command, "@StartedAt", FormatTimestamp(now));
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
            },
            cancellationToken);

        return await this.ExecuteReaderAsync(
            $"SELECT {RunColumns} FROM {JobTableSchema.RunsTable} WHERE ClaimToken = @ClaimToken ORDER BY Id",
            command => DbParameterHelper.Add(command, "@ClaimToken", claimToken),
            ReadRun,
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<JobRun?> InsertClaimedAsync(
        JobRun run, string claimToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var inserted = await this.ExecuteNonQueryAsync(
            this.insertClaimedSql,
            command =>
            {
                BindMaterializeParameters(command, run, JobRunStatus.Claiming);
                DbParameterHelper.Add(command, "@ClaimedAt", FormatTimestamp(now));
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
                DbParameterHelper.Add(command, "@StartedAt", FormatTimestamp(now));
            },
            cancellationToken);

        if (inserted == 0)
        {
            return null;
        }

        return await this.GetRunAsync(run.JobKey, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<HeartbeatResult> HeartbeatAsync(
        long id, string claimToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // One connection for both statements: the renewal proves the claim is still ours, and the
        // read-back that follows answers "was cancellation requested" without a second round-trip
        // from the caller's point of view or a second poll loop.
        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);

        var renewed = await ExecuteNonQueryOnAsync(
            connection,
            $"UPDATE {JobTableSchema.RunsTable} SET ClaimedAt = @ClaimedAt WHERE Id = @Id AND ClaimToken = @ClaimToken",
            command =>
            {
                DbParameterHelper.Add(command, "@ClaimedAt", FormatTimestamp(now));
                DbParameterHelper.Add(command, "@Id", id);
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
            },
            cancellationToken);

        if (renewed == 0)
        {
            return new HeartbeatResult(ClaimHeld: false, CancellationRequested: false);
        }

        var flag = await ExecuteScalarOnAsync(
            connection,
            $"SELECT CancellationRequested FROM {JobTableSchema.RunsTable} WHERE Id = @Id",
            command => DbParameterHelper.Add(command, "@Id", id),
            cancellationToken);

        return new HeartbeatResult(ClaimHeld: true, CancellationRequested: ToBoolean(flag));
    }

    /// <inheritdoc/>
    public async Task CompleteAsync(
        long id, string? scheduleName, DateTimeOffset? occurrence, CancellationToken cancellationToken)
    {
        // Deleting the row and advancing the cursor must be atomic. The row is what stops a
        // concurrent replica materializing this occurrence again; once it is gone, only the cursor
        // does. A crash between the two would re-run a completed occurrence.
        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteNonQueryOnAsync(
            connection,
            $"DELETE FROM {JobTableSchema.RunsTable} WHERE Id = @Id",
            command => DbParameterHelper.Add(command, "@Id", id),
            cancellationToken,
            transaction);

        if (scheduleName is not null && occurrence is { } completed)
        {
            await ExecuteNonQueryOnAsync(
                connection,
                AdvanceCursorSql,
                command =>
                {
                    DbParameterHelper.Add(command, "@LastCompletedOccurrence", FormatTimestamp(completed));
                    DbParameterHelper.Add(command, "@Name", scheduleName);
                },
                cancellationToken,
                transaction);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task ReleaseAsync(long id, string claimToken, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            $"""
            UPDATE {JobTableSchema.RunsTable}
            SET Status = @Pending, ClaimToken = NULL, ClaimedAt = NULL, StartedAt = NULL
            WHERE Id = @Id AND ClaimToken = @ClaimToken
            """,
            command =>
            {
                DbParameterHelper.Add(command, "@Pending", (int)JobRunStatus.Pending);
                DbParameterHelper.Add(command, "@Id", id);
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
            },
            cancellationToken);

    /// <inheritdoc/>
    public async Task<int> IncrementAttemptAsync(
        long id, DateTimeOffset nextRunAt, string error, CancellationToken cancellationToken)
    {
        await this.ExecuteNonQueryAsync(
            $"""
            UPDATE {JobTableSchema.RunsTable}
            SET AttemptCount = AttemptCount + 1, Status = @Pending, RunAt = @RunAt, LastError = @LastError,
                ClaimToken = NULL, ClaimedAt = NULL, StartedAt = NULL
            WHERE Id = @Id
            """,
            command =>
            {
                DbParameterHelper.Add(command, "@Pending", (int)JobRunStatus.Pending);
                DbParameterHelper.Add(command, "@RunAt", FormatTimestamp(nextRunAt));
                DbParameterHelper.Add(command, "@LastError", Truncate(error));
                DbParameterHelper.Add(command, "@Id", id);
            },
            cancellationToken);

        var attempts = await this.ExecuteScalarAsync(
            $"SELECT AttemptCount FROM {JobTableSchema.RunsTable} WHERE Id = @Id",
            command => DbParameterHelper.Add(command, "@Id", id),
            cancellationToken);

        return attempts is null ? 0 : Convert.ToInt32(attempts, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task MarkTerminalAsync(
        long id,
        JobRunStatus status,
        string? error,
        string? scheduleName,
        DateTimeOffset? occurrence,
        CancellationToken cancellationToken)
    {
        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteNonQueryOnAsync(
            connection,
            MarkTerminalSql,
            command =>
            {
                DbParameterHelper.Add(command, "@Status", (int)status);
                DbParameterHelper.Add(command, "@LastError", error is null ? DBNull.Value : Truncate(error));
                DbParameterHelper.Add(command, "@Id", id);
            },
            cancellationToken,
            transaction);

        if (scheduleName is not null && occurrence is { } reached)
        {
            await ExecuteNonQueryOnAsync(
                connection,
                AdvanceCursorSql,
                command =>
                {
                    DbParameterHelper.Add(command, "@LastCompletedOccurrence", FormatTimestamp(reached));
                    DbParameterHelper.Add(command, "@Name", scheduleName);
                },
                cancellationToken,
                transaction);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task AdvanceCursorAsync(
        string scheduleName, DateTimeOffset occurrence, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            AdvanceCursorSql,
            command =>
            {
                DbParameterHelper.Add(command, "@LastCompletedOccurrence", FormatTimestamp(occurrence));
                DbParameterHelper.Add(command, "@Name", scheduleName);
            },
            cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> HasOutstandingAsync(string scheduleName, CancellationToken cancellationToken)
    {
        var rows = await this.ExecuteReaderAsync(
            $"SELECT Id FROM {JobTableSchema.RunsTable} WHERE ScheduleName = @ScheduleName AND Status IN (@Pending, @Claiming)",
            command =>
            {
                DbParameterHelper.Add(command, "@ScheduleName", scheduleName);
                DbParameterHelper.Add(command, "@Pending", (int)JobRunStatus.Pending);
                DbParameterHelper.Add(command, "@Claiming", (int)JobRunStatus.Claiming);
            },
            reader => reader.GetInt64(0),
            cancellationToken);

        return rows.Count > 0;
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> GetNextPendingDueAsync(CancellationToken cancellationToken)
    {
        var earliest = await this.ExecuteScalarAsync(
            $"SELECT MIN(RunAt) FROM {JobTableSchema.RunsTable} WHERE Status = @Pending",
            command => DbParameterHelper.Add(command, "@Pending", (int)JobRunStatus.Pending),
            cancellationToken);

        return earliest is string raw ? ParseTimestamp(raw) : null;
    }

    /// <inheritdoc/>
    public async Task<JobRun?> GetRunAsync(string jobKey, CancellationToken cancellationToken)
    {
        var rows = await this.ExecuteReaderAsync(
            $"SELECT {RunColumns} FROM {JobTableSchema.RunsTable} WHERE JobKey = @JobKey",
            command => DbParameterHelper.Add(command, "@JobKey", jobKey),
            ReadRun,
            cancellationToken);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobRun>> GetInFlightAsync(CancellationToken cancellationToken)
        => await this.ExecuteReaderAsync(
            $"SELECT {RunColumns} FROM {JobTableSchema.RunsTable} WHERE Status = @Claiming ORDER BY Id",
            command => DbParameterHelper.Add(command, "@Claiming", (int)JobRunStatus.Claiming),
            ReadRun,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobRun>> GetFailedAsync(
        string? scheduleName, CancellationToken cancellationToken)
    {
        var filter = scheduleName is null ? string.Empty : " AND ScheduleName = @ScheduleName";
        return await this.ExecuteReaderAsync(
            $"SELECT {RunColumns} FROM {JobTableSchema.RunsTable} WHERE Status = @Failed{filter} ORDER BY Id DESC",
            command =>
            {
                DbParameterHelper.Add(command, "@Failed", (int)JobRunStatus.Failed);
                if (scheduleName is not null)
                {
                    DbParameterHelper.Add(command, "@ScheduleName", scheduleName);
                }
            },
            ReadRun,
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> RequestCancellationAsync(string jobKey, CancellationToken cancellationToken)
    {
        var flagged = await this.ExecuteNonQueryAsync(
            $"UPDATE {JobTableSchema.RunsTable} SET CancellationRequested = 1 WHERE JobKey = @JobKey AND Status IN (@Pending, @Claiming)",
            command =>
            {
                DbParameterHelper.Add(command, "@JobKey", jobKey);
                DbParameterHelper.Add(command, "@Pending", (int)JobRunStatus.Pending);
                DbParameterHelper.Add(command, "@Claiming", (int)JobRunStatus.Claiming);
            },
            cancellationToken);

        return flagged > 0;
    }

    /// <inheritdoc/>
    public Task SetPausedAsync(string name, bool paused, DateTimeOffset now, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            $"UPDATE {JobTableSchema.SchedulesTable} SET IsPaused = @IsPaused, PausedAt = @PausedAt WHERE Name = @Name",
            command =>
            {
                DbParameterHelper.Add(command, "@IsPaused", paused ? 1 : 0);
                DbParameterHelper.Add(command, "@PausedAt", paused ? FormatTimestamp(now) : DBNull.Value);
                DbParameterHelper.Add(command, "@Name", name);
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<int> PurgeTerminalAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            $"DELETE FROM {JobTableSchema.RunsTable} WHERE Status IN (@Failed, @Cancelled) AND RunAt <= @Cutoff",
            command =>
            {
                DbParameterHelper.Add(command, "@Failed", (int)JobRunStatus.Failed);
                DbParameterHelper.Add(command, "@Cancelled", (int)JobRunStatus.Cancelled);
                DbParameterHelper.Add(command, "@Cutoff", FormatTimestamp(olderThan));
            },
            cancellationToken);

    private static string MarkTerminalSql =>
        $"UPDATE {JobTableSchema.RunsTable} SET Status = @Status, LastError = @LastError, ClaimToken = NULL, ClaimedAt = NULL WHERE Id = @Id";

    private static string AdvanceCursorSql =>
        $"UPDATE {JobTableSchema.SchedulesTable} SET LastCompletedOccurrence = @LastCompletedOccurrence WHERE Name = @Name";

    private static string BuildCreateRunsTableSql(ISqlDialect dialect)
        => dialect.BuildCreateTableIfNotExistsSql(
            JobTableSchema.RunsTable,
            $"""
                Id {dialect.BigIntAutoIncrementPrimaryKey},
                JobKey {dialect.KeyTextType} NOT NULL,
                ScheduleName {dialect.KeyTextType},
                Occurrence {dialect.NullableTimestampType},
                MessageType {dialect.TextType} NOT NULL,
                Payload {dialect.TextType} NOT NULL,
                RunAt {dialect.TimestampType} NOT NULL,
                Status {dialect.SmallIntType} NOT NULL DEFAULT 0,
                ClaimedAt {dialect.NullableTimestampType},
                ClaimToken {dialect.NullableTextType},
                AttemptCount {dialect.IntegerType} NOT NULL DEFAULT 0,
                LastError {dialect.NullableTextType},
                CancellationRequested {dialect.SmallIntType} NOT NULL DEFAULT 0,
                StartedAt {dialect.NullableTimestampType},
                CONSTRAINT ux_conduit_job_runs_jobkey UNIQUE (JobKey)
            """);

    private static string BuildCreateSchedulesTableSql(ISqlDialect dialect)
        => dialect.BuildCreateTableIfNotExistsSql(
            JobTableSchema.SchedulesTable,
            $"""
                Name {dialect.KeyTextType} NOT NULL PRIMARY KEY,
                LastCompletedOccurrence {dialect.NullableTimestampType},
                IsPaused {dialect.SmallIntType} NOT NULL DEFAULT 0,
                PausedAt {dialect.NullableTimestampType}
            """);

    private static void BindMaterializeParameters(
        DbCommand command, JobRun run, JobRunStatus? statusOverride = null)
    {
        DbParameterHelper.Add(command, "@JobKey", run.JobKey);
        DbParameterHelper.Add(command, "@ScheduleName", (object?)run.ScheduleName ?? DBNull.Value);
        DbParameterHelper.Add(command, "@Occurrence",
            run.Occurrence is { } occurrence ? FormatTimestamp(occurrence) : DBNull.Value);
        DbParameterHelper.Add(command, "@MessageType", run.MessageType);
        DbParameterHelper.Add(command, "@Payload", run.Payload);
        DbParameterHelper.Add(command, "@RunAt", FormatTimestamp(run.RunAt));
        DbParameterHelper.Add(command, "@Status", (int)(statusOverride ?? run.Status));
        DbParameterHelper.Add(command, "@AttemptCount", run.AttemptCount);
        DbParameterHelper.Add(command, "@CancellationRequested", run.CancellationRequested ? 1 : 0);
    }

    private static async Task<int> ExecuteNonQueryOnAsync(
        DbConnection connection,
        string commandText,
        Action<DbCommand>? configureParameters,
        CancellationToken cancellationToken,
        DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // SQL comes from the dialect and module-owned identifiers, never user input.
        command.CommandText = commandText;
#pragma warning restore CA2100
        command.Transaction = transaction;
        configureParameters?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ExecuteScalarOnAsync(
        DbConnection connection,
        string commandText,
        Action<DbCommand>? configureParameters,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // SQL comes from the dialect and module-owned identifiers, never user input.
        command.CommandText = commandText;
#pragma warning restore CA2100
        configureParameters?.Invoke(command);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static string FormatTimestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string raw)
        => new(
            DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
            TimeSpan.Zero);

    private static bool ToBoolean(object? value)
        => value is not null
            && value != DBNull.Value
            && Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;

    // Every engine's text type is unbounded except SQL Server's NVARCHAR(MAX) and MySQL's LONGTEXT,
    // which are too — but an unbounded exception message is still worth capping, since a stack-laden
    // ToString() is not what an operator reads and the column is queried by IJobs.
    private static string Truncate(string error)
        => error.Length <= 4000 ? error : error[..4000];

    private static JobScheduleRow ReadScheduleRow(DbDataReader reader)
    {
        var occurrenceOrdinal = reader.GetOrdinal("LastCompletedOccurrence");
        var pausedAtOrdinal = reader.GetOrdinal("PausedAt");

        return new JobScheduleRow
        {
            Name = reader.GetString(reader.GetOrdinal("Name")),
            LastCompletedOccurrence = reader.IsDBNull(occurrenceOrdinal)
                ? null
                : ParseTimestamp(reader.GetString(occurrenceOrdinal)),
            IsPaused = ToBoolean(reader.GetValue(reader.GetOrdinal("IsPaused"))),
            PausedAt = reader.IsDBNull(pausedAtOrdinal)
                ? null
                : ParseTimestamp(reader.GetString(pausedAtOrdinal)),
        };
    }

    private static JobRun ReadRun(DbDataReader reader)
    {
        var scheduleNameOrdinal = reader.GetOrdinal("ScheduleName");
        var occurrenceOrdinal = reader.GetOrdinal("Occurrence");
        var claimedAtOrdinal = reader.GetOrdinal("ClaimedAt");
        var claimTokenOrdinal = reader.GetOrdinal("ClaimToken");
        var lastErrorOrdinal = reader.GetOrdinal("LastError");
        var startedAtOrdinal = reader.GetOrdinal("StartedAt");

        return new JobRun
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            JobKey = reader.GetString(reader.GetOrdinal("JobKey")),
            ScheduleName = reader.IsDBNull(scheduleNameOrdinal) ? null : reader.GetString(scheduleNameOrdinal),
            Occurrence = reader.IsDBNull(occurrenceOrdinal)
                ? null
                : ParseTimestamp(reader.GetString(occurrenceOrdinal)),
            MessageType = reader.GetString(reader.GetOrdinal("MessageType")),
            Payload = reader.GetString(reader.GetOrdinal("Payload")),
            RunAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("RunAt"))),
            // Status maps to the engine's small-int type, whose native CLR width varies (SQL Server
            // TINYINT -> byte, Postgres SMALLINT -> short), so convert provider-agnostically.
            Status = (JobRunStatus)Convert.ToInt32(
                reader.GetValue(reader.GetOrdinal("Status")), CultureInfo.InvariantCulture),
            ClaimedAt = reader.IsDBNull(claimedAtOrdinal)
                ? null
                : ParseTimestamp(reader.GetString(claimedAtOrdinal)),
            ClaimToken = reader.IsDBNull(claimTokenOrdinal) ? null : reader.GetString(claimTokenOrdinal),
            AttemptCount = reader.GetInt32(reader.GetOrdinal("AttemptCount")),
            LastError = reader.IsDBNull(lastErrorOrdinal) ? null : reader.GetString(lastErrorOrdinal),
            CancellationRequested = ToBoolean(reader.GetValue(reader.GetOrdinal("CancellationRequested"))),
            StartedAt = reader.IsDBNull(startedAtOrdinal)
                ? null
                : ParseTimestamp(reader.GetString(startedAtOrdinal)),
        };
    }
}
