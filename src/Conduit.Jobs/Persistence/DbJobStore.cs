using System.Data;
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

    private const string ScopeColumns = "ScopeKey, ScopeId, TriggerName, MessageType, Payload, DueAt, Version";

    private static readonly string[] InsertScopeColumns =
        ["ScopeKey", "ScopeId", "TriggerName", "MessageType", "Payload", "DueAt", "Version"];

    private const string HistoryColumns =
        "JobKey, ScheduleName, MessageType, Outcome, RunAt, StartedAt, CompletedAt, AttemptCount, LastError";

    private readonly IOptions<JobsOptions> options;
    private readonly string createRunsTableSql;
    private readonly string createSchedulesTableSql;
    private readonly string claimDueSql;
    private readonly string materializeSql;
    private readonly string insertClaimedSql;
    private readonly string reconcileScheduleSql;
    private readonly string scheduleExistsSql;
    private readonly string createScopesTableSql;
    private readonly string insertScopeSql;
    private readonly string dueScopesSql;
    private readonly string createHistoryTableSql;
    private readonly string queryHistoryPageSql;

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
        this.scheduleExistsSql = dialect.BuildExistsSql(JobTableSchema.SchedulesTable, "Name");
        this.createScopesTableSql = BuildCreateScopesTableSql(dialect);
        this.insertScopeSql = dialect.BuildInsertOrIgnoreSql(
            JobTableSchema.ScopesTable, InsertScopeColumns, "ScopeKey");
        this.dueScopesSql =
            $"SELECT {ScopeColumns} FROM {JobTableSchema.ScopesTable} " +
            $"WHERE DueAt <= @Now ORDER BY DueAt {dialect.PaginationSuffix}";
        this.createHistoryTableSql = BuildCreateHistoryTableSql(dialect);
        this.queryHistoryPageSql =
            $"""
            SELECT {HistoryColumns} FROM {JobTableSchema.HistoryTable}
            WHERE (@JobKey IS NULL OR JobKey = @JobKey)
              AND (@ScheduleName IS NULL OR ScheduleName = @ScheduleName)
              AND (@Outcome IS NULL OR Outcome = @Outcome)
              AND (@Since IS NULL OR CompletedAt >= @Since)
              AND (@Until IS NULL OR CompletedAt <= @Until)
            ORDER BY CompletedAt DESC
            {dialect.PaginationSuffix}
            """;
    }

    /// <inheritdoc/>
    public async Task EnsureTablesExistAsync(CancellationToken cancellationToken)
    {
        await this.ExecuteNonQueryAsync(this.createSchedulesTableSql, configureParameters: null, cancellationToken);
        await this.ExecuteNonQueryAsync(this.createRunsTableSql, configureParameters: null, cancellationToken);
        await this.ExecuteNonQueryAsync(this.createScopesTableSql, configureParameters: null, cancellationToken);
        await this.ExecuteNonQueryAsync(this.createHistoryTableSql, configureParameters: null, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ReconcileSchedulesAsync(
        IReadOnlyList<string> scheduleNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scheduleNames);

        foreach (var name in scheduleNames)
        {
            await this.EnsureScheduleRowAsync(name, cancellationToken);
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
    public async Task<MaterializeOutcome> TryMaterializeWithoutOverlapAsync(
        JobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var scheduleName = run.ScheduleName
            ?? throw new ArgumentException(
                "A run materialized without overlap must name its schedule; the schedule's row is the lock.",
                nameof(run));

        // A second pass happens only when the first found no schedule row, so it locked nothing and
        // decided nothing (LockScheduleRowAsync tells "no row" from "locked" on every engine). Every
        // declared schedule's row is reconciled at startup, so that means the row was never reconciled
        // (a schedule declared only on a host that has not started) or something outside Conduit
        // deleted it.
        for (var pass = 0; pass < 2; pass++)
        {
            if (await this.TryMaterializeUnderScheduleLockAsync(run, scheduleName, cancellationToken)
                is { } outcome)
            {
                return outcome;
            }

            // Created outside any transaction, then the transaction starts over. Inserting it inside
            // would be wrong twice over on MySQL: INSERT IGNORE on a row another host has just
            // inserted takes a shared lock on it, and two transactions each holding one deadlock on
            // the exclusive lock that follows; and under REPEATABLE READ the transaction's existence
            // check would already have fixed its read view before it held the lock, so the overlap
            // count could miss a run committed in between.
            await this.EnsureScheduleRowAsync(scheduleName, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Job schedule '{scheduleName}' has no row in {JobTableSchema.SchedulesTable} to lock, even " +
            "straight after inserting one, so something is deleting it. A run cannot be materialized " +
            "without overlap unless the schedule's row exists.");
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
                DbParameterHelper.Add(command, "@Now", FormatTimestamp(now));
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
        JobRun run, string? scheduleName, DateTimeOffset? occurrence, bool retainRunKey, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Deleting the row and advancing the cursor must be atomic. The row is what stops a
        // concurrent replica materializing this occurrence again; once it is gone, only the cursor
        // does. A crash between the two would re-run a completed occurrence. The history row is
        // written in the same transaction: a crash cannot land the ledger row deleted with no
        // permanent record of the outcome, or a history record whose run was never actually deleted.
        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The cursor first: lock order is the schedule's row before any run row (see
        // TryMaterializeUnderScheduleLockAsync). Deleting the run first would hold its row while
        // waiting for the schedule's — the reverse of a materialization holding the schedule's row
        // while its overlap check reads the runs table, which on SQL Server's locking READ COMMITTED
        // waits on that same deleted row. Not hypothetical: with the cursor last, the persistence
        // matrix's NeitherDeadlockNorOverlapWhenMaterializationsRaceACompletion deadlocks on SQL Server.
        // The advance is forward-only; when the cursor is already past this occurrence it may lock
        // nothing, and then this transaction never waits on the schedule's row at all, so it still
        // cannot close a cycle — and a late materializer of this occurrence reads a cursor past it.
        await AdvanceCursorOnAsync(connection, transaction, scheduleName, occurrence, cancellationToken);

        await WriteHistoryAsync(
            connection, transaction, run, JobRunOutcome.Completed, error: null, now, cancellationToken);

        // A kept row drops its claim, so it is invisible to every query that looks for live work —
        // all of them filter on Pending or Claiming — and visible only to the unique key.
        await ExecuteNonQueryOnAsync(
            connection,
            retainRunKey
                ? $"UPDATE {JobTableSchema.RunsTable} SET Status = @Completed, ClaimToken = NULL, ClaimedAt = NULL WHERE Id = @Id"
                : $"DELETE FROM {JobTableSchema.RunsTable} WHERE Id = @Id",
            command =>
            {
                DbParameterHelper.Add(command, "@Id", run.Id);
                if (retainRunKey)
                {
                    DbParameterHelper.Add(command, "@Completed", (int)JobRunStatus.Completed);
                }
            },
            cancellationToken,
            transaction);

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
        JobRun run,
        JobRunStatus status,
        string? error,
        string? scheduleName,
        DateTimeOffset? occurrence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The cursor first, for the lock order CompleteAsync explains.
        await AdvanceCursorOnAsync(connection, transaction, scheduleName, occurrence, cancellationToken);

        await WriteHistoryAsync(
            connection, transaction, run, ToOutcome(status), error, now, cancellationToken);

        await ExecuteNonQueryOnAsync(
            connection,
            MarkTerminalSql,
            command =>
            {
                DbParameterHelper.Add(command, "@Status", (int)status);
                DbParameterHelper.Add(command, "@LastError", error is null ? DBNull.Value : Truncate(error));
                DbParameterHelper.Add(command, "@Id", run.Id);
            },
            cancellationToken,
            transaction);

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
    public async Task<bool> TryInsertScopeAsync(JobScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var inserted = await this.ExecuteNonQueryAsync(
            this.insertScopeSql,
            command =>
            {
                DbParameterHelper.Add(command, "@ScopeKey", scope.ScopeKey);
                DbParameterHelper.Add(command, "@ScopeId", scope.ScopeId);
                DbParameterHelper.Add(command, "@TriggerName", scope.TriggerName);
                DbParameterHelper.Add(command, "@MessageType", scope.MessageType);
                DbParameterHelper.Add(command, "@Payload", scope.Payload);
                DbParameterHelper.Add(command, "@DueAt", FormatTimestamp(scope.DueAt));
                DbParameterHelper.Add(command, "@Version", scope.Version);
            },
            cancellationToken);

        return inserted > 0;
    }

    /// <inheritdoc/>
    public async Task<JobScope?> GetScopeAsync(string scopeKey, CancellationToken cancellationToken)
    {
        var rows = await this.ExecuteReaderAsync(
            $"SELECT {ScopeColumns} FROM {JobTableSchema.ScopesTable} WHERE ScopeKey = @ScopeKey",
            command => DbParameterHelper.Add(command, "@ScopeKey", scopeKey),
            ReadScope,
            cancellationToken);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc/>
    public async Task<bool> TryMergeScopeAsync(
        string scopeKey, string scopeId, string payload, int version, CancellationToken cancellationToken)
    {
        // DueAt is untouched on purpose: merging is leading-edge, so a steady stream of events cannot
        // keep pushing the run away. The guard is on (ScopeId, Version), not Version alone: Version
        // resets to 0 every time a scope is deleted and reopened, so a merge that straddles a pickup
        // could otherwise match the row that replaced its target.
        var merged = await this.ExecuteNonQueryAsync(
            $"""
            UPDATE {JobTableSchema.ScopesTable}
            SET Payload = @Payload, Version = Version + 1
            WHERE ScopeKey = @ScopeKey AND ScopeId = @ScopeId AND Version = @Version
            """,
            command =>
            {
                DbParameterHelper.Add(command, "@Payload", payload);
                DbParameterHelper.Add(command, "@ScopeKey", scopeKey);
                DbParameterHelper.Add(command, "@ScopeId", scopeId);
                DbParameterHelper.Add(command, "@Version", version);
            },
            cancellationToken);

        return merged > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> TryDeleteScopeAsync(
        string scopeKey, string scopeId, int version, CancellationToken cancellationToken)
    {
        var deleted = await this.ExecuteNonQueryAsync(
            $"""
            DELETE FROM {JobTableSchema.ScopesTable}
            WHERE ScopeKey = @ScopeKey AND ScopeId = @ScopeId AND Version = @Version
            """,
            command =>
            {
                DbParameterHelper.Add(command, "@ScopeKey", scopeKey);
                DbParameterHelper.Add(command, "@ScopeId", scopeId);
                DbParameterHelper.Add(command, "@Version", version);
            },
            cancellationToken);

        return deleted > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobScope>> GetDueScopesAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        => await this.ExecuteReaderAsync(
            this.dueScopesSql,
            command =>
            {
                DbParameterHelper.Add(command, "@Now", FormatTimestamp(now));
                DbParameterHelper.Add(command, "@PageSize", batchSize);
                DbParameterHelper.Add(command, "@Offset", 0);
            },
            ReadScope,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> GetNextScopeDueAsync(CancellationToken cancellationToken)
    {
        var earliest = await this.ExecuteScalarAsync(
            $"SELECT MIN(DueAt) FROM {JobTableSchema.ScopesTable}",
            configureParameters: null,
            cancellationToken);

        return earliest is string raw ? ParseTimestamp(raw) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobScope>> GetScopesAsync(
        string? triggerName, CancellationToken cancellationToken)
    {
        var filter = triggerName is null ? string.Empty : " WHERE TriggerName = @TriggerName";
        return await this.ExecuteReaderAsync(
            $"SELECT {ScopeColumns} FROM {JobTableSchema.ScopesTable}{filter} ORDER BY DueAt",
            command =>
            {
                if (triggerName is not null)
                {
                    DbParameterHelper.Add(command, "@TriggerName", triggerName);
                }
            },
            ReadScope,
            cancellationToken);
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
    public async Task<IReadOnlyList<JobRun>> GetPendingAsync(
        string? scheduleName, CancellationToken cancellationToken)
    {
        var filter = scheduleName is null ? string.Empty : " AND ScheduleName = @ScheduleName";

        // Ordered by RunAt, not Id: a queue reads soonest-first, where history reads newest-first.
        return await this.ExecuteReaderAsync(
            $"SELECT {RunColumns} FROM {JobTableSchema.RunsTable} WHERE Status = @Pending{filter} ORDER BY RunAt",
            command =>
            {
                DbParameterHelper.Add(command, "@Pending", (int)JobRunStatus.Pending);
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

    /// <inheritdoc/>
    public Task<int> PurgeCompletedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            $"DELETE FROM {JobTableSchema.RunsTable} WHERE Status = @Completed AND RunAt <= @Cutoff",
            command =>
            {
                DbParameterHelper.Add(command, "@Completed", (int)JobRunStatus.Completed);
                DbParameterHelper.Add(command, "@Cutoff", FormatTimestamp(olderThan));
            },
            cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> TryClearTerminalRunAsync(string jobKey, CancellationToken cancellationToken)
    {
        // Guarded on Status IN (Failed, Cancelled), the same pair PurgeTerminalAsync targets: a
        // Pending or claimed row is live work and must never be deleted out from under it.
        var cleared = await this.ExecuteNonQueryAsync(
            $"DELETE FROM {JobTableSchema.RunsTable} WHERE JobKey = @JobKey AND Status IN (@Failed, @Cancelled)",
            command =>
            {
                DbParameterHelper.Add(command, "@JobKey", jobKey);
                DbParameterHelper.Add(command, "@Failed", (int)JobRunStatus.Failed);
                DbParameterHelper.Add(command, "@Cancelled", (int)JobRunStatus.Cancelled);
            },
            cancellationToken);

        return cleared > 0;
    }

    /// <inheritdoc/>
    public async Task<JobHistoryPage> QueryHistoryAsync(
        JobHistoryFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var offset = 0;
        if (filter.PageToken is not null
            && int.TryParse(filter.PageToken, CultureInfo.InvariantCulture, out var parsedOffset))
        {
            offset = parsedOffset;
        }

        var countResult = await this.ExecuteScalarAsync(
            CountHistorySql,
            command => AddHistoryFilterParameters(command, filter),
            cancellationToken);
        var totalCount = Convert.ToInt32(countResult, CultureInfo.InvariantCulture);

        var entries = await this.ExecuteReaderAsync(
            this.queryHistoryPageSql,
            command =>
            {
                AddHistoryFilterParameters(command, filter);
                DbParameterHelper.Add(command, "@PageSize", filter.PageSize);
                DbParameterHelper.Add(command, "@Offset", offset);
            },
            ReadHistory,
            cancellationToken);

        var nextOffset = offset + entries.Count;
        var nextPageToken = nextOffset < totalCount
            ? nextOffset.ToString(CultureInfo.InvariantCulture)
            : null;

        return new JobHistoryPage
        {
            Entries = entries,
            NextPageToken = nextPageToken,
            TotalCount = totalCount,
        };
    }

    /// <inheritdoc/>
    public Task<int> PurgeHistoryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            PurgeHistorySql,
            command => DbParameterHelper.Add(command, "@Cutoff", FormatTimestamp(olderThan)),
            cancellationToken);

    /// <summary>
    /// One attempt at the overlap protocol: lock the schedule's row, check, insert, in one transaction.
    /// </summary>
    /// <returns>
    /// The outcome, or <see langword="null"/> when the schedule's row does not exist — the lock then
    /// locked nothing, so nothing was decided and nothing was written.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why this is race-free.</b> Every write of a no-overlap schedule's run goes through here, and
    /// each one holds the schedule's row lock from before its overlap check until its insert commits.
    /// So the checks and inserts for one schedule run one at a time, and each check reads the committed
    /// result of every insert before it. "At most one run outstanding" then holds by induction: a run
    /// only <i>becomes</i> outstanding by being inserted (release and retry keep it outstanding; nothing
    /// turns a terminal row live again), and an insert happens only after observing none outstanding
    /// under a different key. The no-op <c>UPDATE</c> is the lock because it is the one portable
    /// statement that takes a row's write lock on every engine; no per-engine SQL is involved.
    /// </para>
    /// <para>
    /// Per engine, at the default isolation level (none is pinned, matching
    /// <see cref="CompleteAsync"/>):
    /// SQLite — <c>BeginTransaction</c> is <c>BEGIN IMMEDIATE</c>, the database's single write lock,
    /// held to commit; everything after it reads the latest commit.
    /// SQL Server (READ COMMITTED, locking or snapshot) — the <c>UPDATE</c> holds the row's exclusive
    /// lock to commit; each later statement sees everything committed before it began, which includes
    /// the previous holder's insert.
    /// PostgreSQL (READ COMMITTED) — the <c>UPDATE</c> row lock blocks the next holder until commit;
    /// every statement takes a fresh snapshot after it is acquired.
    /// MySQL/InnoDB (REPEATABLE READ) — the <c>UPDATE</c> takes the row's exclusive record lock (a
    /// locking read, so it creates no read view); the read view is fixed by the first plain
    /// <c>SELECT</c>, which runs only once the lock is held.
    /// Under a stricter default (SERIALIZABLE or snapshot transactions) the waiter gets a
    /// serialization error instead of a stale read: a failed call, never a second run.
    /// </para>
    /// <para>
    /// <b>Deadlock freedom</b> rests on lock order: the schedule's row before any run row.
    /// <see cref="CompleteAsync"/> and <see cref="MarkTerminalAsync"/> advance the cursor first for
    /// that reason. Every other write to the runs table is one statement that never waits on a
    /// schedule row, so it cannot close a cycle.
    /// </para>
    /// <para>
    /// <b>A missing row</b> is the one case where the <c>UPDATE</c> locks nothing; see
    /// <see cref="LockScheduleRowAsync"/>, which re-takes the lock once the row exists rather than
    /// trusting a probe that found it.
    /// </para>
    /// <para>
    /// <b>Residual windows.</b> A run of the schedule written without this method — a host still on a
    /// version before it, or a declaration that says <c>AllowOverlap()</c> on one host and not on
    /// another — takes no lock and is outside the guarantee. Declarations must agree across hosts,
    /// as they already must for occurrences to. During a rolling upgrade on SQL Server at locking READ
    /// COMMITTED there is a second effect: an older host's <c>CompleteAsync</c> still advances the cursor
    /// last, so it can deadlock with this method. If the completion is the victim, its claim goes stale
    /// and the run executes again after <c>ClaimTimeout</c>; if this method is, the occurrence is logged
    /// and skipped. Transient, and confined to the rollout. A schedule row deleted and re-created by
    /// something outside Conduit mid-call is another window (see <see cref="LockScheduleRowAsync"/>).
    /// The cursor check has one window of its own: a host starting up counts an occurrence as missed
    /// once it is due, and under <c>MisfirePolicy.Skip</c> (the default, and every interval schedule's)
    /// advances the cursor to it — so a running host whose timer for that same occurrence fires a
    /// moment later finds the cursor already there, and nobody runs it. The window is the running
    /// host's timer latency plus a round trip, or the clock skew between the hosts if larger; the cost
    /// is one skipped occurrence of that schedule. Moving the cursor to now after an outage of more than
    /// <c>MaxMisfireWalk</c> occurrences behaves the same way. Accepted, with no grace period.
    /// </para>
    /// </remarks>
    private async Task<MaterializeOutcome?> TryMaterializeUnderScheduleLockAsync(
        JobRun run, string scheduleName, CancellationToken cancellationToken)
    {
        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await this.LockScheduleRowAsync(connection, transaction, scheduleName, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        // An occurrence's row is deleted on success, so once it has completed only the cursor says so.
        // Read under the lock: CompleteAsync and MarkTerminalAsync advance the cursor under the same
        // lock, first in the transaction that deletes or terminally marks the row, so a replica whose
        // timer fired after another finished the occurrence sees the cursor rather than re-inserting it.
        // A starting host's misfire skip also moves the cursor, so "at or past" is all this can say.
        if (run.Occurrence is { } occurrence
            && await ExecuteScalarOnAsync(
                connection,
                ReadCursorSql,
                command => DbParameterHelper.Add(command, "@Name", scheduleName),
                cancellationToken,
                transaction) is string cursor
            && ParseTimestamp(cursor) >= occurrence)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MaterializeOutcome.AlreadyCompleted;
        }

        var overlapping = await ExecuteScalarOnAsync(
            connection,
            CountOverlappingSql,
            command =>
            {
                DbParameterHelper.Add(command, "@ScheduleName", scheduleName);
                DbParameterHelper.Add(command, "@JobKey", run.JobKey);
                DbParameterHelper.Add(command, "@Pending", (int)JobRunStatus.Pending);
                DbParameterHelper.Add(command, "@Claiming", (int)JobRunStatus.Claiming);
            },
            cancellationToken,
            transaction);
        if (Convert.ToInt64(overlapping, CultureInfo.InvariantCulture) > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MaterializeOutcome.Overlapping;
        }

        var inserted = await ExecuteNonQueryOnAsync(
            connection,
            this.materializeSql,
            command => BindMaterializeParameters(command, run),
            cancellationToken,
            transaction);

        await transaction.CommitAsync(cancellationToken);
        return inserted > 0 ? MaterializeOutcome.Materialized : MaterializeOutcome.AlreadyMaterialized;
    }

    /// <summary>Advances a schedule run's cursor inside the caller's transaction; a no-op otherwise.</summary>
    private static Task AdvanceCursorOnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string? scheduleName,
        DateTimeOffset? occurrence,
        CancellationToken cancellationToken)
        => scheduleName is not null && occurrence is { } reached
            ? ExecuteNonQueryOnAsync(
                connection,
                AdvanceCursorSql,
                command =>
                {
                    DbParameterHelper.Add(command, "@LastCompletedOccurrence", FormatTimestamp(reached));
                    DbParameterHelper.Add(command, "@Name", scheduleName);
                },
                cancellationToken,
                transaction)
            : Task.CompletedTask;

    /// <summary>
    /// Takes the schedule row's write lock for the rest of the transaction. <see langword="false"/> means
    /// there is no row, so nothing is locked and nothing may be decided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A positive count proves the lock on every engine: the <c>UPDATE</c> matched the row. Zero is
    /// ambiguous. Either there was no row, so nothing was locked, or the row was locked and MySQL counted
    /// changed rather than matched rows (<c>UseAffectedRows=true</c>); SQL Server under
    /// <c>SET NOCOUNT ON</c> reports -1 the same way. So zero is followed by a probe.
    /// </para>
    /// <para>
    /// A probe that finds the row does <b>not</b> prove the lock. On PostgreSQL and SQL Server at READ
    /// COMMITTED, an <c>UPDATE</c> that matches nothing locks nothing, and the next statement sees a row
    /// committed after it: another caller ensuring the row in between leaves this one holding nothing
    /// while that caller locks, checks and inserts. So the lock is taken again; now that the row exists
    /// it matches and waits for any holder. Where the first <c>UPDATE</c> already held it (MySQL counting
    /// changed rows), the second is a no-op re-lock. MySQL at REPEATABLE READ cannot see the row appear
    /// after its first <c>UPDATE</c> anyway: that <c>UPDATE</c>'s gap lock blocks the insert (at READ
    /// COMMITTED it takes none, behaves like PostgreSQL, and the re-lock covers it). On SQLite
    /// <c>BEGIN IMMEDIATE</c> already excludes every other writer.
    /// </para>
    /// <para>
    /// A second zero, with the row gone again, is a row deleted between two statements of this call.
    /// Conduit never deletes a schedule row, so only something outside it can do that; the call then
    /// reports the row missing rather than proceed. Only a delete <i>and</i> re-insert, both from outside
    /// Conduit, between the re-lock and its probe could still leave a call unlocked.
    /// </para>
    /// </remarks>
    private async Task<bool> LockScheduleRowAsync(
        DbConnection connection, DbTransaction transaction, string scheduleName, CancellationToken cancellationToken)
    {
        if (await ExecuteLockAsync(connection, transaction, scheduleName, cancellationToken) > 0)
        {
            return true;
        }

        if (!await this.ScheduleRowExistsAsync(connection, transaction, scheduleName, cancellationToken))
        {
            return false;
        }

        return await ExecuteLockAsync(connection, transaction, scheduleName, cancellationToken) > 0
            || await this.ScheduleRowExistsAsync(connection, transaction, scheduleName, cancellationToken);
    }

    private static Task<int> ExecuteLockAsync(
        DbConnection connection, DbTransaction transaction, string scheduleName, CancellationToken cancellationToken)
        => ExecuteNonQueryOnAsync(
            connection,
            LockScheduleSql,
            command => DbParameterHelper.Add(command, "@Name", scheduleName),
            cancellationToken,
            transaction);

    private async Task<bool> ScheduleRowExistsAsync(
        DbConnection connection, DbTransaction transaction, string scheduleName, CancellationToken cancellationToken)
        => await ExecuteScalarOnAsync(
            connection,
            this.scheduleExistsSql,
            command => DbParameterHelper.Add(command, "@Name", scheduleName),
            cancellationToken,
            transaction) is not (null or DBNull);

    private Task<int> EnsureScheduleRowAsync(string scheduleName, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            this.reconcileScheduleSql,
            command =>
            {
                DbParameterHelper.Add(command, "@Name", scheduleName);
                DbParameterHelper.Add(command, "@IsPaused", 0);
            },
            cancellationToken);

    // Sets a column to itself: it changes nothing, but on every engine an UPDATE that matches a row
    // takes that row's write lock and holds it until the transaction ends.
    private static string LockScheduleSql =>
        $"UPDATE {JobTableSchema.SchedulesTable} SET IsPaused = IsPaused WHERE Name = @Name";

    private static string ReadCursorSql =>
        $"SELECT LastCompletedOccurrence FROM {JobTableSchema.SchedulesTable} WHERE Name = @Name";

    private static string CountOverlappingSql =>
        $"SELECT COUNT(*) FROM {JobTableSchema.RunsTable} " +
        "WHERE ScheduleName = @ScheduleName AND Status IN (@Pending, @Claiming) AND JobKey <> @JobKey";

    private static string MarkTerminalSql =>
        $"UPDATE {JobTableSchema.RunsTable} SET Status = @Status, LastError = @LastError, ClaimToken = NULL, ClaimedAt = NULL WHERE Id = @Id";

    // Forward only. Runs of an AllowOverlap() schedule can finish out of order, and an unconditional SET
    // let the earlier one move the cursor back behind the later — after which a replica late for the
    // later occurrence would find neither its row nor a cursor past it, and run it again. The text
    // comparison is chronological: every value is the same fixed-width round-trip ISO 8601 UTC form,
    // the same property the claim's RunAt cutoff already relies on.
    private static string AdvanceCursorSql =>
        $"UPDATE {JobTableSchema.SchedulesTable} SET LastCompletedOccurrence = @LastCompletedOccurrence " +
        "WHERE Name = @Name AND (LastCompletedOccurrence IS NULL OR LastCompletedOccurrence < @LastCompletedOccurrence)";

    private static string InsertHistorySql =>
        $"""
        INSERT INTO {JobTableSchema.HistoryTable}
            (JobKey, ScheduleName, MessageType, Outcome, RunAt, StartedAt, CompletedAt, AttemptCount, LastError)
        VALUES
            (@JobKey, @ScheduleName, @MessageType, @Outcome, @RunAt, @StartedAt, @CompletedAt, @AttemptCount, @LastError)
        """;

    private static string CountHistorySql =>
        $"""
        SELECT COUNT(*) FROM {JobTableSchema.HistoryTable}
        WHERE (@JobKey IS NULL OR JobKey = @JobKey)
          AND (@ScheduleName IS NULL OR ScheduleName = @ScheduleName)
          AND (@Outcome IS NULL OR Outcome = @Outcome)
          AND (@Since IS NULL OR CompletedAt >= @Since)
          AND (@Until IS NULL OR CompletedAt <= @Until)
        """;

    private static string PurgeHistorySql =>
        $"DELETE FROM {JobTableSchema.HistoryTable} WHERE CompletedAt <= @Cutoff";

    /// <summary>
    /// Writes a history row on the same connection/transaction as the caller's terminal-state write,
    /// so the outcome is captured atomically with — and therefore cannot outlive or be outlived by —
    /// the ledger transition it records.
    /// </summary>
    private static Task<int> WriteHistoryAsync(
        DbConnection connection,
        DbTransaction transaction,
        JobRun run,
        JobRunOutcome outcome,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => ExecuteNonQueryOnAsync(
            connection,
            InsertHistorySql,
            command =>
            {
                DbParameterHelper.Add(command, "@JobKey", run.JobKey);
                DbParameterHelper.Add(command, "@ScheduleName", (object?)run.ScheduleName ?? DBNull.Value);
                DbParameterHelper.Add(command, "@MessageType", run.MessageType);
                DbParameterHelper.Add(command, "@Outcome", (int)outcome);
                DbParameterHelper.Add(command, "@RunAt", FormatTimestamp(run.RunAt));
                DbParameterHelper.Add(command, "@StartedAt",
                    run.StartedAt is { } startedAt ? FormatTimestamp(startedAt) : DBNull.Value);
                DbParameterHelper.Add(command, "@CompletedAt", FormatTimestamp(now));
                DbParameterHelper.Add(command, "@AttemptCount", run.AttemptCount);
                DbParameterHelper.Add(command, "@LastError", error is null ? DBNull.Value : Truncate(error));
            },
            cancellationToken,
            transaction);

    private static JobRunOutcome ToOutcome(JobRunStatus status) => status switch
    {
        JobRunStatus.Failed => JobRunOutcome.Failed,
        JobRunStatus.Cancelled => JobRunOutcome.Cancelled,
        JobRunStatus.Pending or JobRunStatus.Claiming or JobRunStatus.Completed => throw new ArgumentOutOfRangeException(
            nameof(status), status, "MarkTerminalAsync only accepts Failed or Cancelled."),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unrecognized JobRunStatus."),
    };

    private static void AddHistoryFilterParameters(DbCommand command, JobHistoryFilter filter)
    {
        // Explicit DbType on every optional filter, even when null: some providers (Npgsql) cannot
        // infer the type of an untyped DBNull used in an `IS NULL` predicate and fail with
        // "could not determine data type of parameter".
        DbParameterHelper.Add(command, "@JobKey", (object?)filter.JobKey ?? DBNull.Value, DbType.String);
        DbParameterHelper.Add(command, "@ScheduleName", (object?)filter.ScheduleName ?? DBNull.Value, DbType.String);
        DbParameterHelper.Add(
            command, "@Outcome", filter.Outcome is { } outcome ? (int)outcome : DBNull.Value, DbType.Int32);
        DbParameterHelper.Add(command, "@Since", filter.Since is { } since
            ? FormatTimestamp(since)
            : DBNull.Value, DbType.String);
        DbParameterHelper.Add(command, "@Until", filter.Until is { } until
            ? FormatTimestamp(until)
            : DBNull.Value, DbType.String);
    }

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

    private static string BuildCreateScopesTableSql(ISqlDialect dialect)
        => dialect.BuildCreateTableIfNotExistsSql(
            JobTableSchema.ScopesTable,
            $"""
                ScopeKey {dialect.KeyTextType} NOT NULL PRIMARY KEY,
                ScopeId {dialect.KeyTextType} NOT NULL,
                TriggerName {dialect.KeyTextType} NOT NULL,
                MessageType {dialect.TextType} NOT NULL,
                Payload {dialect.TextType} NOT NULL,
                DueAt {dialect.TimestampType} NOT NULL,
                Version {dialect.IntegerType} NOT NULL DEFAULT 0
            """);

    private static string BuildCreateHistoryTableSql(ISqlDialect dialect)
        => dialect.BuildCreateTableIfNotExistsSql(
            JobTableSchema.HistoryTable,
            $"""
                Id {dialect.BigIntAutoIncrementPrimaryKey},
                JobKey {dialect.KeyTextType} NOT NULL,
                ScheduleName {dialect.KeyTextType},
                MessageType {dialect.TextType} NOT NULL,
                Outcome {dialect.SmallIntType} NOT NULL,
                RunAt {dialect.TimestampType} NOT NULL,
                StartedAt {dialect.NullableTimestampType},
                CompletedAt {dialect.TimestampType} NOT NULL,
                AttemptCount {dialect.IntegerType} NOT NULL DEFAULT 0,
                LastError {dialect.NullableTextType}
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
        CancellationToken cancellationToken,
        DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // SQL comes from the dialect and module-owned identifiers, never user input.
        command.CommandText = commandText;
#pragma warning restore CA2100
        command.Transaction = transaction;
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

    private static JobScope ReadScope(DbDataReader reader)
        => new()
        {
            ScopeKey = reader.GetString(reader.GetOrdinal("ScopeKey")),
            ScopeId = reader.GetString(reader.GetOrdinal("ScopeId")),
            TriggerName = reader.GetString(reader.GetOrdinal("TriggerName")),
            MessageType = reader.GetString(reader.GetOrdinal("MessageType")),
            Payload = reader.GetString(reader.GetOrdinal("Payload")),
            DueAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("DueAt"))),
            Version = reader.GetInt32(reader.GetOrdinal("Version")),
        };

    private static JobRunHistoryInfo ReadHistory(DbDataReader reader)
    {
        var scheduleNameOrdinal = reader.GetOrdinal("ScheduleName");
        var startedAtOrdinal = reader.GetOrdinal("StartedAt");
        var lastErrorOrdinal = reader.GetOrdinal("LastError");

        return new JobRunHistoryInfo(
            reader.GetString(reader.GetOrdinal("JobKey")),
            reader.IsDBNull(scheduleNameOrdinal) ? null : reader.GetString(scheduleNameOrdinal),
            reader.GetString(reader.GetOrdinal("MessageType")),
            // Outcome maps to the engine's small-int type, whose native CLR width varies (SQL Server
            // TINYINT -> byte, Postgres SMALLINT -> short), so convert provider-agnostically.
            (JobRunOutcome)Convert.ToInt32(
                reader.GetValue(reader.GetOrdinal("Outcome")), CultureInfo.InvariantCulture),
            ParseTimestamp(reader.GetString(reader.GetOrdinal("RunAt"))),
            reader.IsDBNull(startedAtOrdinal) ? null : ParseTimestamp(reader.GetString(startedAtOrdinal)),
            ParseTimestamp(reader.GetString(reader.GetOrdinal("CompletedAt"))),
            reader.GetInt32(reader.GetOrdinal("AttemptCount")),
            reader.IsDBNull(lastErrorOrdinal) ? null : reader.GetString(lastErrorOrdinal));
    }
}
