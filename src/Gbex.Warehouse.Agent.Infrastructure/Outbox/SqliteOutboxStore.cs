using Gbex.Warehouse.Agent.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Gbex.Warehouse.Agent.Infrastructure.Outbox;

/// <summary>
/// Durable SQLite-backed outbox. Never stores the station secret — only
/// operation metadata, a sanitized JSON payload, and (for evidence uploads)
/// a path to a temporary file on disk.
///
/// Concurrency: ClaimNextAsync's UPDATE ... WHERE Id = (SELECT ... LIMIT 1)
/// RETURNING * is ONE statement. SQLite serializes writers (a single
/// RESERVED/EXCLUSIVE lock per write transaction), so two workers calling
/// this concurrently cannot both claim the same row: whichever commits
/// second re-evaluates its subquery under the lock and finds the row is no
/// longer Pending, claiming nothing instead.
/// </summary>
public sealed class SqliteOutboxStore : IOutboxStore, IDisposable
{
    private readonly string _connectionString;
    private readonly IClock _clock;
    private readonly ILogger<SqliteOutboxStore> _logger;

    public SqliteOutboxStore(string databasePath, IClock clock, ILogger<SqliteOutboxStore> logger)
    {
        // Pooling=false: each OpenConnection() call does a real open/close,
        // so the underlying OS file handle is released the instant a
        // connection is disposed — no reliance on ClearAllPools() being
        // called at the right moment. This outbox is a low-throughput,
        // one-operation-at-a-time path, so the extra open/close cost is
        // negligible. Found via a REAL Windows CI failure: pooled
        // connections kept station.secret's sibling outbox.db locked after
        // disposal, so a second store instance opened against the same
        // path (e.g. after an app "restart") — or simply cleaning up a test
        // temp directory — hit "the process cannot access the file" on
        // Windows. Never reproduced on macOS/Linux, where file deletion
        // doesn't require the last handle to be closed first.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        _clock = clock;
        _logger = logger;
        Initialize();
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // WAL mode: better concurrent read/write behavior for a long-running
        // background processor alongside occasional UI-triggered reads.
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();

        using var createTable = connection.CreateCommand();
        createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS OutboxItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OperationType TEXT NOT NULL,
                GbexBarcode TEXT NOT NULL,
                MeasurementId TEXT NULL,
                EasyCubePackageNumber TEXT NULL,
                DeviceId TEXT NULL,
                IdempotencyKey TEXT NOT NULL UNIQUE,
                SanitizedPayloadJson TEXT NOT NULL,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                NextAttemptAt TEXT NOT NULL,
                LastSanitizedError TEXT NULL,
                EvidenceFilePath TEXT NULL,
                State TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_OutboxItems_State_NextAttemptAt ON OutboxItems(State, NextAttemptAt);
            """;
        createTable.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<OutboxItem> EnqueueAsync(NewOutboxItem item, CancellationToken ct)
    {
        var existing = await FindByIdempotencyKeyAsync(item.IdempotencyKey, ct);
        if (existing is not null)
        {
            // Idempotent enqueue — a logical operation being enqueued twice
            // (e.g. the workflow engine retrying after a crash before the
            // first enqueue's caller even knew it succeeded) must not create
            // a second row.
            return existing;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO OutboxItems
                (OperationType, GbexBarcode, MeasurementId, EasyCubePackageNumber, DeviceId,
                 IdempotencyKey, SanitizedPayloadJson, RetryCount, NextAttemptAt, EvidenceFilePath, State, CreatedAt)
            VALUES
                ($operationType, $gbexBarcode, $measurementId, $packageNumber, $deviceId,
                 $idempotencyKey, $payload, 0, $nextAttemptAt, $evidencePath, 'Pending', $createdAt)
            RETURNING Id, CreatedAt;
            """;
        var now = _clock.UtcNow;
        command.Parameters.AddWithValue("$operationType", item.OperationType.ToString());
        command.Parameters.AddWithValue("$gbexBarcode", item.GbexBarcode);
        command.Parameters.AddWithValue("$measurementId", (object?)item.MeasurementId ?? DBNull.Value);
        command.Parameters.AddWithValue("$packageNumber", (object?)item.EasyCubePackageNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$deviceId", (object?)item.DeviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$idempotencyKey", item.IdempotencyKey);
        command.Parameters.AddWithValue("$payload", item.SanitizedPayloadJson);
        command.Parameters.AddWithValue("$nextAttemptAt", now.ToString("O"));
        command.Parameters.AddWithValue("$evidencePath", (object?)item.EvidenceFilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));

        try
        {
            using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var id = reader.GetInt64(0);
            return ToItem(id, item, 0, now, null, OutboxItemState.Pending, now);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT (unique) */)
        {
            // Lost a race with another enqueue of the same key — fetch and return the winner.
            var winner = await FindByIdempotencyKeyAsync(item.IdempotencyKey, ct);
            return winner ?? throw new InvalidOperationException("Unique constraint hit but row not found — should not happen.");
        }
    }

    public async Task<OutboxItem?> ClaimNextAsync(CancellationToken ct)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OutboxItems
            SET State = 'InProgress'
            WHERE Id = (
                SELECT Id FROM OutboxItems
                WHERE State = 'Pending' AND NextAttemptAt <= $now
                ORDER BY Id ASC
                LIMIT 1
            )
            RETURNING Id, OperationType, GbexBarcode, MeasurementId, EasyCubePackageNumber, DeviceId,
                      IdempotencyKey, SanitizedPayloadJson, RetryCount, NextAttemptAt, LastSanitizedError,
                      EvidenceFilePath, State, CreatedAt;
            """;
        command.Parameters.AddWithValue("$now", _clock.UtcNow.ToString("O"));

        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return ReadItem(reader);
    }

    public async Task MarkCompletedAsync(long id, CancellationToken ct) =>
        await UpdateStateAsync(id, "Completed", null, null, ct);

    public async Task MarkFailedAndRescheduleAsync(long id, string sanitizedError, DateTimeOffset nextAttemptAt, CancellationToken ct)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OutboxItems
            SET State = 'Pending', RetryCount = RetryCount + 1, LastSanitizedError = $error, NextAttemptAt = $next
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$error", sanitizedError);
        command.Parameters.AddWithValue("$next", nextAttemptAt.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkRequiresReauthorizationAsync(long id, string sanitizedError, CancellationToken ct) =>
        await UpdateStateAsync(id, "RequiresReauthorization", sanitizedError, null, ct);

    public async Task MarkRequiresManualResolutionAsync(long id, string sanitizedError, CancellationToken ct) =>
        await UpdateStateAsync(id, "RequiresManualResolution", sanitizedError, null, ct);

    private async Task UpdateStateAsync(long id, string state, string? sanitizedError, DateTimeOffset? nextAttemptAt, CancellationToken ct)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OutboxItems
            SET State = $state, LastSanitizedError = COALESCE($error, LastSanitizedError)
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$error", (object?)sanitizedError ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountPendingAsync(CancellationToken ct)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM OutboxItems WHERE State IN ('Pending', 'InProgress');";
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public async Task<int> CountByStateAsync(OutboxItemState state, CancellationToken ct)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM OutboxItems WHERE State = $state;";
        command.Parameters.AddWithValue("$state", state.ToString());
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<string>> GetRecentSanitizedErrorsAsync(int limit, CancellationToken ct)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LastSanitizedError FROM OutboxItems
            WHERE LastSanitizedError IS NOT NULL
            ORDER BY Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<string>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    public async Task<OutboxItem?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OperationType, GbexBarcode, MeasurementId, EasyCubePackageNumber, DeviceId,
                   IdempotencyKey, SanitizedPayloadJson, RetryCount, NextAttemptAt, LastSanitizedError,
                   EvidenceFilePath, State, CreatedAt
            FROM OutboxItems WHERE IdempotencyKey = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
    }

    private static OutboxItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        OperationType = Enum.Parse<OutboxOperationType>(reader.GetString(1)),
        GbexBarcode = reader.GetString(2),
        MeasurementId = reader.IsDBNull(3) ? null : reader.GetString(3),
        EasyCubePackageNumber = reader.IsDBNull(4) ? null : reader.GetString(4),
        DeviceId = reader.IsDBNull(5) ? null : reader.GetString(5),
        IdempotencyKey = reader.GetString(6),
        SanitizedPayloadJson = reader.GetString(7),
        RetryCount = reader.GetInt32(8),
        NextAttemptAt = DateTimeOffset.Parse(reader.GetString(9)),
        LastSanitizedError = reader.IsDBNull(10) ? null : reader.GetString(10),
        EvidenceFilePath = reader.IsDBNull(11) ? null : reader.GetString(11),
        State = Enum.Parse<OutboxItemState>(reader.GetString(12)),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(13)),
    };

    private static OutboxItem ToItem(long id, NewOutboxItem item, int retryCount, DateTimeOffset nextAttemptAt, string? error, OutboxItemState state, DateTimeOffset createdAt) => new()
    {
        Id = id,
        OperationType = item.OperationType,
        GbexBarcode = item.GbexBarcode,
        MeasurementId = item.MeasurementId,
        EasyCubePackageNumber = item.EasyCubePackageNumber,
        DeviceId = item.DeviceId,
        IdempotencyKey = item.IdempotencyKey,
        SanitizedPayloadJson = item.SanitizedPayloadJson,
        RetryCount = retryCount,
        NextAttemptAt = nextAttemptAt,
        LastSanitizedError = error,
        EvidenceFilePath = item.EvidenceFilePath,
        State = state,
        CreatedAt = createdAt,
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
