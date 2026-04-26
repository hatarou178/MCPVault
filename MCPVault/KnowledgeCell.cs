using Microsoft.Data.Sqlite;

public sealed class KnowledgeCell : IDisposable
{
    private readonly SqliteConnection _db;

    public KnowledgeCell(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        ApplySchema();
    }

    private void ApplySchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS knowledge_cells (
                cell_name   TEXT    NOT NULL,
                key         TEXT    NOT NULL,
                value       TEXT    NOT NULL,
                updated_at  INTEGER NOT NULL,
                PRIMARY KEY (cell_name, key)
            );
            CREATE INDEX IF NOT EXISTS idx_kcell_cell    ON knowledge_cells (cell_name);
            CREATE INDEX IF NOT EXISTS idx_kcell_updated ON knowledge_cells (updated_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Write(string cellName, string key, string value)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO knowledge_cells (cell_name, key, value, updated_at)
            VALUES ($cell, $key, $val, $now)
            ON CONFLICT(cell_name, key) DO UPDATE SET
                value      = excluded.value,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$cell", cellName);
        cmd.Parameters.AddWithValue("$key",  key);
        cmd.Parameters.AddWithValue("$val",  value);
        cmd.Parameters.AddWithValue("$now",  now);
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<(string Key, string Value, long UpdatedAt)> Read(string cellName, string? key = null)
    {
        using var cmd = _db.CreateCommand();
        if (key is null)
        {
            cmd.CommandText = """
                SELECT key, value, updated_at FROM knowledge_cells
                WHERE cell_name = $cell
                ORDER BY updated_at DESC
                """;
            cmd.Parameters.AddWithValue("$cell", cellName);
        }
        else
        {
            cmd.CommandText = """
                SELECT key, value, updated_at FROM knowledge_cells
                WHERE cell_name = $cell AND key = $key
                """;
            cmd.Parameters.AddWithValue("$cell", cellName);
            cmd.Parameters.AddWithValue("$key",  key);
        }
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string, long)>();
        while (r.Read())
            result.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return result;
    }

    public int Delete(string cellName, string? key = null)
    {
        using var cmd = _db.CreateCommand();
        if (key is null)
        {
            cmd.CommandText = "DELETE FROM knowledge_cells WHERE cell_name = $cell";
            cmd.Parameters.AddWithValue("$cell", cellName);
        }
        else
        {
            cmd.CommandText = "DELETE FROM knowledge_cells WHERE cell_name = $cell AND key = $key";
            cmd.Parameters.AddWithValue("$cell", cellName);
            cmd.Parameters.AddWithValue("$key",  key);
        }
        return cmd.ExecuteNonQuery();
    }

    public IEnumerable<(string CellName, int KeyCount, long LastUpdated)> List()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT cell_name, COUNT(*) AS key_count, MAX(updated_at) AS last_updated
            FROM knowledge_cells
            GROUP BY cell_name
            ORDER BY last_updated DESC
            """;
        using var r = cmd.ExecuteReader();
        var result = new List<(string, int, long)>();
        while (r.Read())
            result.Add((r.GetString(0), r.GetInt32(1), r.GetInt64(2)));
        return result;
    }

    public void Dispose() => _db.Dispose();
}
