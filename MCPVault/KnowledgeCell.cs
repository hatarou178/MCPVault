using Microsoft.Data.Sqlite;

public sealed class KnowledgeCell : IDisposable
{
    private readonly SqliteConnection _db;

    // kcell_rollback で戻せる操作数（セルごと）
    private const int HistoryKeep = 20;

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
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS knowledge_cells (
                cell_name   TEXT    NOT NULL,
                key         TEXT    NOT NULL,
                value       TEXT    NOT NULL,
                updated_at  INTEGER NOT NULL,
                PRIMARY KEY (cell_name, key)
            );
            CREATE INDEX IF NOT EXISTS idx_kcell_cell    ON knowledge_cells (cell_name);
            CREATE INDEX IF NOT EXISTS idx_kcell_updated ON knowledge_cells (updated_at);

            -- kcell_rollback 用の操作履歴（1 操作 = kcell_ops 1 行 + 変更前のキー値 n 行）
            CREATE TABLE IF NOT EXISTS kcell_ops (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                cell_name   TEXT    NOT NULL,
                op          TEXT    NOT NULL,
                created_at  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS kcell_op_items (
                op_id           INTEGER NOT NULL REFERENCES kcell_ops(id) ON DELETE CASCADE,
                key             TEXT    NOT NULL,
                prev_value      TEXT,       -- NULL = 操作前はキーが存在しなかった
                prev_updated_at INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_kcell_ops_cell   ON kcell_ops      (cell_name, id);
            CREATE INDEX IF NOT EXISTS idx_kcell_items_op   ON kcell_op_items (op_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>1 キーの値と更新日時を返す。存在しなければ null。</summary>
    public (string Value, long UpdatedAt)? Get(string cellName, string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value, updated_at FROM knowledge_cells WHERE cell_name = $cell AND key = $key";
        cmd.Parameters.AddWithValue("$cell", cellName);
        cmd.Parameters.AddWithValue("$key",  key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt64(1)) : null;
    }

    /// <summary>値を書き込む（upsert）。op は履歴に残す操作名（write / append / edit）。</summary>
    public void Write(string cellName, string key, string value, string op = "write")
    {
        using var tx = _db.BeginTransaction();
        RecordOp(tx, cellName, op, [key]);

        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
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
        cmd.Parameters.AddWithValue("$now",  DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();

        tx.Commit();
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
        List<string> keys = key is null
            ? Read(cellName).Select(e => e.Key).ToList()
            : Get(cellName, key) is null ? [] : [key];
        if (keys.Count == 0) return 0;

        using var tx = _db.BeginTransaction();
        RecordOp(tx, cellName, "delete", keys);

        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
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
        int deleted = cmd.ExecuteNonQuery();

        tx.Commit();
        return deleted;
    }

    /// <summary>
    /// セルへの直前の操作を 1 回分取り消す（キーの値と更新日時を操作前に戻す）。
    /// 戻した操作名・対象キー・残り履歴数を返す。履歴がなければ null。
    /// </summary>
    public (string Op, List<string> Keys, int Remaining)? Rollback(string cellName)
    {
        using var tx = _db.BeginTransaction();

        using var find = _db.CreateCommand();
        find.Transaction = tx;
        find.CommandText = "SELECT id, op FROM kcell_ops WHERE cell_name = $cell ORDER BY id DESC LIMIT 1";
        find.Parameters.AddWithValue("$cell", cellName);
        long opId; string op;
        using (var r = find.ExecuteReader())
        {
            if (!r.Read()) return null;
            opId = r.GetInt64(0);
            op   = r.GetString(1);
        }

        var items = new List<(string Key, string? PrevValue, long? PrevUpdatedAt)>();
        using (var sel = _db.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT key, prev_value, prev_updated_at FROM kcell_op_items WHERE op_id = $op";
            sel.Parameters.AddWithValue("$op", opId);
            using var r = sel.ExecuteReader();
            while (r.Read())
                items.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
        }

        foreach (var (key, prevValue, prevUpdatedAt) in items)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            if (prevValue is null)
            {
                cmd.CommandText = "DELETE FROM knowledge_cells WHERE cell_name = $cell AND key = $key";
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO knowledge_cells (cell_name, key, value, updated_at)
                    VALUES ($cell, $key, $val, $ua)
                    ON CONFLICT(cell_name, key) DO UPDATE SET
                        value      = excluded.value,
                        updated_at = excluded.updated_at
                    """;
                cmd.Parameters.AddWithValue("$val", prevValue);
                cmd.Parameters.AddWithValue("$ua",  prevUpdatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            cmd.Parameters.AddWithValue("$cell", cellName);
            cmd.Parameters.AddWithValue("$key",  key);
            cmd.ExecuteNonQuery();
        }

        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM kcell_ops WHERE id = $op";
            del.Parameters.AddWithValue("$op", opId);
            del.ExecuteNonQuery();
        }

        int remaining;
        using (var cnt = _db.CreateCommand())
        {
            cnt.Transaction = tx;
            cnt.CommandText = "SELECT COUNT(*) FROM kcell_ops WHERE cell_name = $cell";
            cnt.Parameters.AddWithValue("$cell", cellName);
            remaining = Convert.ToInt32(cnt.ExecuteScalar());
        }

        tx.Commit();
        return (op, items.Select(i => i.Key).ToList(), remaining);
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

    // 操作前のキー値を履歴に記録し、セルごとに HistoryKeep 件を超えた古い操作を削除する
    private void RecordOp(SqliteTransaction tx, string cellName, string op, IEnumerable<string> keys)
    {
        long opId;
        using (var ins = _db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO kcell_ops (cell_name, op, created_at) VALUES ($cell, $op, $now);
                SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$cell", cellName);
            ins.Parameters.AddWithValue("$op",   op);
            ins.Parameters.AddWithValue("$now",  DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            opId = (long)ins.ExecuteScalar()!;
        }

        foreach (var key in keys)
        {
            using var item = _db.CreateCommand();
            item.Transaction = tx;
            item.CommandText = """
                INSERT INTO kcell_op_items (op_id, key, prev_value, prev_updated_at)
                SELECT $op, $key, value, updated_at FROM knowledge_cells WHERE cell_name = $cell AND key = $key
                UNION ALL
                SELECT $op, $key, NULL, NULL WHERE NOT EXISTS
                    (SELECT 1 FROM knowledge_cells WHERE cell_name = $cell AND key = $key)
                """;
            item.Parameters.AddWithValue("$op",   opId);
            item.Parameters.AddWithValue("$cell", cellName);
            item.Parameters.AddWithValue("$key",  key);
            item.ExecuteNonQuery();
        }

        using var prune = _db.CreateCommand();
        prune.Transaction = tx;
        prune.CommandText = """
            DELETE FROM kcell_ops
            WHERE cell_name = $cell
              AND id NOT IN (SELECT id FROM kcell_ops WHERE cell_name = $cell ORDER BY id DESC LIMIT $keep)
            """;
        prune.Parameters.AddWithValue("$cell", cellName);
        prune.Parameters.AddWithValue("$keep", HistoryKeep);
        prune.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
