using System.Text.Json;
using Contracts;
using Microsoft.Data.Sqlite;

namespace Core.Measurement;

public sealed class SqliteResultStore : IResultStore, IRunFinalizer, IDisposable
{
    public bool InjectWriteFailure { get; set; }
    private void CheckWrite() { if (InjectWriteFailure) throw new IOException("가상 SQLite 쓰기 실패 주입"); }
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SqliteResultStore(string path)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        if (path == ":memory:") connection.Open();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var db = await OpenAsync(ct);
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS runs (run_id TEXT PRIMARY KEY, recipe_json TEXT NOT NULL, phase_totals TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS wafers (run_id TEXT NOT NULL, wafer_id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(run_id, wafer_id));
                CREATE TABLE IF NOT EXISTS sites (run_id TEXT NOT NULL, wafer_id TEXT NOT NULL, site_index INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(run_id, wafer_id, site_index));
                CREATE TABLE IF NOT EXISTS log (run_id TEXT NOT NULL, seq INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(run_id, seq));
                CREATE INDEX IF NOT EXISTS idx_wafers_run_id ON wafers(run_id);
                CREATE INDEX IF NOT EXISTS idx_sites_run_id ON sites(run_id);
                CREATE INDEX IF NOT EXISTS idx_log_run_id ON log(run_id);
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { gate.Release(); }
    }

    public Task SaveRunAsync(RunRecord run, CancellationToken ct) => WriteRunAsync(run, ct, true);
    public Task FinalizeRunAsync(RunRecord run, CancellationToken ct) => WriteRunAsync(run, ct, false);
    private async Task WriteRunAsync(RunRecord run, CancellationToken ct, bool replaceChildren)
    {
        CheckWrite();
        await gate.WaitAsync(ct);
        try
        {
            var db = await OpenAsync(ct);
            using var transaction = await db.BeginTransactionAsync(ct);
            // 같은 실행 ID를 저장하면 기존 자식 데이터까지 지우고 새 실행으로 덮어쓴다.
            if (replaceChildren) foreach (var table in new[] { "sites", "wafers", "log" })
            {
                using var delete = db.CreateCommand();
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE run_id=$id";
                delete.Parameters.AddWithValue("$id", run.RunId);
                await delete.ExecuteNonQueryAsync(ct);
            }
            using var command = db.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT OR REPLACE INTO runs VALUES ($id,$recipe,$phases,$payload)";
            command.Parameters.AddWithValue("$id", run.RunId);
            command.Parameters.AddWithValue("$recipe", run.RecipeJson);
            command.Parameters.AddWithValue("$phases", JsonSerializer.Serialize(run.PhaseTotals, JsonSettings.Options));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(run, JsonSettings.Options));
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task SaveWaferAsync(string runId, WaferSummary wafer, CancellationToken ct)
    {
        CheckWrite();
        await gate.WaitAsync(ct);
        try
        {
            var db = await OpenAsync(ct);
            using var transaction = await db.BeginTransactionAsync(ct);
            using (var command = db.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "INSERT OR REPLACE INTO wafers VALUES ($run,$wafer,$payload)";
                command.Parameters.AddWithValue("$run", runId);
                command.Parameters.AddWithValue("$wafer", wafer.WaferId);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(wafer with { Sites = Array.Empty<MeasurementResult>() }, JsonSettings.Options));
                await command.ExecuteNonQueryAsync(ct);
            }
            foreach (var site in wafer.Sites)
            {
                using var command = db.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "INSERT OR REPLACE INTO sites VALUES ($run,$wafer,$site,$payload)";
                command.Parameters.AddWithValue("$run", runId);
                command.Parameters.AddWithValue("$wafer", wafer.WaferId);
                command.Parameters.AddWithValue("$site", site.SiteIndex);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(site, JsonSettings.Options));
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task SaveLogAsync(string runId, IReadOnlyList<StepEvent> log, CancellationToken ct)
    {
        CheckWrite();
        await gate.WaitAsync(ct);
        try
        {
            var db = await OpenAsync(ct);
            using var transaction = await db.BeginTransactionAsync(ct);
            foreach (var item in log)
            {
                using var command = db.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "INSERT OR REPLACE INTO log VALUES ($run,$seq,$payload)";
                command.Parameters.AddWithValue("$run", runId);
                command.Parameters.AddWithValue("$seq", item.Seq);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item, JsonSettings.Options));
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, string? runId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var command = (await OpenAsync(ct)).CreateCommand();
            command.CommandText = sql;
            if (runId is not null) command.Parameters.AddWithValue("$run", runId);
            using var reader = await command.ExecuteReaderAsync(ct);
            var results = new List<T>();
            while (await reader.ReadAsync(ct)) results.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonSettings.Options)!);
            return results;
        }
        finally { gate.Release(); }
    }

    public Task<IReadOnlyList<RunRecord>> ListRunsAsync(CancellationToken ct) => ReadAsync<RunRecord>("SELECT payload FROM runs ORDER BY rowid", null, ct);
    public Task<IReadOnlyList<StepEvent>> GetLogAsync(string runId, CancellationToken ct) => ReadAsync<StepEvent>("SELECT payload FROM log WHERE run_id=$run ORDER BY seq", runId, ct);
    public async Task<IReadOnlyList<WaferSummary>> GetWafersAsync(string runId, CancellationToken ct)
    {
        var wafers = await ReadAsync<WaferSummary>("SELECT payload FROM wafers WHERE run_id=$run ORDER BY rowid", runId, ct);
        var sites = await ReadAsync<MeasurementResult>("SELECT payload FROM sites WHERE run_id=$run ORDER BY wafer_id,site_index", runId, ct);
        return wafers.Select(w => w with { Sites = sites.Where(s => s.WaferId == w.WaferId).ToArray() }).ToArray();
    }
    public void Dispose() { connection.Dispose(); gate.Dispose(); }
}
