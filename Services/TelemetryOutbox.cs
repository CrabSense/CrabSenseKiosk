using System.Text.Json;
using CrabSenseKiosk.DTOs;
using Microsoft.Data.Sqlite;

namespace CrabSenseKiosk.Services;

public sealed class TelemetryOutbox
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TelemetryOutbox(IConfiguration configuration)
    {
        var path = configuration["Kiosk:DataPath"] ?? "data/kiosk.db";
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
    }

    public async Task EnqueueAsync(TelemetrySnapshot snapshot, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = Open();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO telemetry_outbox(event_id, device_code, payload, created_at)
                VALUES ($event, $device, $payload, $created);
                """;
            var eventId = Guid.NewGuid().ToString("N");
            cmd.Parameters.AddWithValue("$event", eventId);
            cmd.Parameters.AddWithValue("$device", snapshot.DeviceCode);
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot));
            cmd.Parameters.AddWithValue("$created", snapshot.ReceivedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<(long Id, string EventId, TelemetrySnapshot Snapshot)>> PendingAsync(
        int take, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = Open();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT id, event_id, payload FROM telemetry_outbox
                ORDER BY id LIMIT $take;
                """;
            cmd.Parameters.AddWithValue("$take", take);
            await using var rows = await cmd.ExecuteReaderAsync(ct);
            var result = new List<(long, string, TelemetrySnapshot)>();
            while (await rows.ReadAsync(ct))
            {
                var snapshot = JsonSerializer.Deserialize<TelemetrySnapshot>(
                    rows.GetString(2));
                if (snapshot is not null)
                    result.Add((rows.GetInt64(0), rows.GetString(1), snapshot));
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = Open();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM telemetry_outbox WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private void Initialize()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS telemetry_outbox(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                device_code TEXT NOT NULL,
                payload TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_telemetry_outbox_id
                ON telemetry_outbox(id);
            """;
        cmd.ExecuteNonQuery();
        try
        {
            using var alter = db.CreateCommand();
            alter.CommandText = "ALTER TABLE telemetry_outbox ADD COLUMN event_id TEXT;";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Existing installations already have the current schema.
        }
    }
}
