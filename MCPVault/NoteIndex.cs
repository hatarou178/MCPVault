using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public enum UpsertResult { Added, Updated, Unchanged }

/// <summary>
/// Obsidianノートのファイルインデックスを管理するクラス。
/// </summary>
public sealed class NoteIndex : IDisposable
{
    private readonly SqliteConnection _db;

    public NoteIndex(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        ApplySchema();
        SeedDefaults();
    }

    // -------------------------------------------------------------------------
    // スキーマ・初期データ
    // -------------------------------------------------------------------------

    private void ApplySchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS notes (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                relative_path    TEXT    NOT NULL UNIQUE,
                folder           TEXT,
                filename         TEXT    NOT NULL,
                last_modified    INTEGER NOT NULL,
                indexed_at       INTEGER NOT NULL,
                content          TEXT,
                content_hash     TEXT
            );

            CREATE TABLE IF NOT EXISTS excluded_folders (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                path             TEXT    NOT NULL UNIQUE,
                note             TEXT
            );

            CREATE TABLE IF NOT EXISTS note_meta (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                note_id INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
                key     TEXT    NOT NULL,
                value   TEXT
            );

            CREATE TABLE IF NOT EXISTS note_headings (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                note_id INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
                level   INTEGER NOT NULL,
                text    TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS note_aliases (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                note_id INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
                alias   TEXT    NOT NULL
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(
                content,
                tokenize='trigram'
            );

            CREATE INDEX IF NOT EXISTS idx_notes_folder        ON notes         (folder);
            CREATE INDEX IF NOT EXISTS idx_notes_last_modified ON notes         (last_modified);
            CREATE INDEX IF NOT EXISTS idx_note_meta_note_id   ON note_meta     (note_id);
            CREATE INDEX IF NOT EXISTS idx_headings_note_id    ON note_headings (note_id);
            CREATE INDEX IF NOT EXISTS idx_aliases_note_id     ON note_aliases  (note_id);
            """;
        cmd.ExecuteNonQuery();
    }

    // 初回のみデフォルト除外フォルダを登録
    private void SeedDefaults()
    {
        using var check = _db.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM excluded_folders";
        if ((long)check.ExecuteScalar()! > 0) return;

        foreach (var (path, note) in DefaultExcludedFolders)
            AddExcludedFolder(path, note);
    }

    private static readonly (string path, string note)[] DefaultExcludedFolders =
    [
        (".obsidian",   "Obsidianシステムフォルダ"),
        (".trash",      "削除済みノート"),
    ];

    // -------------------------------------------------------------------------
    // notes テーブル操作
    // -------------------------------------------------------------------------

    /// <summary>存在すれば更新、なければ挿入する。</summary>
    public void Upsert(NoteEntry entry)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notes (relative_path, folder, filename, last_modified, indexed_at, content, content_hash)
            VALUES ($path, $folder, $filename, $lm, $ia, $content, $hash)
            ON CONFLICT(relative_path) DO UPDATE SET
                folder        = excluded.folder,
                filename      = excluded.filename,
                last_modified = excluded.last_modified,
                indexed_at    = excluded.indexed_at,
                content       = excluded.content,
                content_hash  = excluded.content_hash
            """;
        BindNoteEntry(cmd, entry);
        cmd.ExecuteNonQuery();

        long noteId = GetNoteId(entry.RelativePath)!.Value;

        // FTS5 同期（rowid で既存エントリを削除してから再挿入）
        FtsDelete(noteId);
        if (entry.Content != null)
            FtsInsert(noteId, entry.Content);

        // サブテーブル再構築（content がある場合のみ）
        if (entry.Content != null)
        {
            DeleteRelated(noteId);
            var (meta, aliases, headings) = ParseNote(entry.Content);
            InsertMeta(noteId, meta);
            InsertAliases(noteId, aliases);
            InsertHeadings(noteId, headings);
        }
    }

    /// <summary>
    /// last_modified が DB の値より新しい場合、またはコンテンツが未取得の場合に Upsert する。
    /// 新規追加・更新・変更なしを返す。
    /// </summary>
    public UpsertResult UpsertIfNewer(NoteEntry entry)
    {
        long? existingLm;
        bool contentMissing;

        using (var check = _db.CreateCommand())
        {
            check.CommandText = "SELECT last_modified, content FROM notes WHERE relative_path = $path";
            check.Parameters.AddWithValue("$path", entry.RelativePath);
            using var r = check.ExecuteReader();
            if (!r.Read())
            {
                existingLm     = null;
                contentMissing = false;
            }
            else
            {
                existingLm     = r.GetInt64(0);
                // content が NULL かつ今回は content あり → 再取得が必要
                contentMissing = r.IsDBNull(1) && entry.Content != null;
            }
        }

        if (existingLm == null)
        {
            Upsert(entry);
            return UpsertResult.Added;
        }

        if (existingLm >= entry.LastModified && !contentMissing)
            return UpsertResult.Unchanged;

        Upsert(entry);
        return UpsertResult.Updated;
    }

    /// <summary>相対パスでノートを1件取得する。見つからない場合は null。</summary>
    public NoteEntry? Get(string relativePath)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT relative_path, folder, filename, last_modified, indexed_at, content, content_hash
            FROM notes WHERE relative_path = $path
            """;
        cmd.Parameters.AddWithValue("$path", relativePath);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadNoteEntry(r) : null;
    }

    /// <summary>フォルダ配下のノート一覧を返す。folder が null の場合は全件。</summary>
    public IEnumerable<NoteEntry> List(string? folder = null)
    {
        using var cmd = _db.CreateCommand();
        if (folder is null)
        {
            cmd.CommandText = """
                SELECT relative_path, folder, filename, last_modified, indexed_at, content, content_hash
                FROM notes ORDER BY relative_path
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT relative_path, folder, filename, last_modified, indexed_at, content, content_hash
                FROM notes WHERE folder = $folder ORDER BY relative_path
                """;
            cmd.Parameters.AddWithValue("$folder", folder);
        }
        using var r = cmd.ExecuteReader();
        var result = new List<NoteEntry>();
        while (r.Read()) result.Add(ReadNoteEntry(r));
        return result;
    }

    /// <summary>指定パスのノートをインデックスから削除する。</summary>
    public void Delete(string relativePath)
    {
        // FTS5 は CASCADE の対象外なので先に削除
        long? noteId = GetNoteId(relativePath);
        if (noteId.HasValue) FtsDelete(noteId.Value);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE relative_path = $path";
        cmd.Parameters.AddWithValue("$path", relativePath);
        cmd.ExecuteNonQuery();
        // note_meta / note_headings / note_aliases は ON DELETE CASCADE で自動削除
    }

    /// <summary>ノートが存在するフォルダパスの一覧を返す（重複なし・昇順）。</summary>
    public IEnumerable<string> ListFolders()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT folder FROM notes
            WHERE folder IS NOT NULL
            ORDER BY folder
            """;
        using var r = cmd.ExecuteReader();
        var result = new List<string>();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    /// <summary>全ノートの相対パス一覧を返す（コンテンツは含まない）。</summary>
    public IEnumerable<string> ListPaths()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT relative_path FROM notes ORDER BY relative_path";
        using var r = cmd.ExecuteReader();
        var result = new List<string>();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    /// <summary>フォルダのリネーム／移動に伴い、配下ノートのパスをインデックス上で一括更新する。</summary>
    /// <returns>更新されたノート件数</returns>
    public int RenameFolder(string oldPath, string newPath)
    {
        string oldSlash  = oldPath + "/";
        string newSlash  = newPath + "/";
        int    startPos  = oldSlash.Length + 1;   // SQLite SUBSTR は 1-indexed

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE notes
            SET
                relative_path = $newSlash || SUBSTR(relative_path, $startPos),
                folder = CASE
                    WHEN folder = $oldPath              THEN $newPath
                    WHEN folder LIKE $oldSlash || '%'  THEN $newSlash || SUBSTR(folder, $startPos)
                    ELSE folder
                END
            WHERE relative_path LIKE $oldSlash || '%'
            """;
        cmd.Parameters.AddWithValue("$oldPath",  oldPath);
        cmd.Parameters.AddWithValue("$newPath",  newPath);
        cmd.Parameters.AddWithValue("$oldSlash", oldSlash);
        cmd.Parameters.AddWithValue("$newSlash", newSlash);
        cmd.Parameters.AddWithValue("$startPos", startPos);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>実ファイルと照合し、存在しないパスのレコードを一括削除する。</summary>
    public int Purge(IEnumerable<string> existingPaths)
    {
        var keep = existingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = List().Select(n => n.RelativePath).Where(p => !keep.Contains(p)).ToList();
        foreach (var p in stale) Delete(p);
        return stale.Count;
    }

    /// <summary>last_modified の降順で上位 count 件を返す。</summary>
    public IEnumerable<(string RelativePath, string Filename, long LastModified)> RecentNotes(int count)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT relative_path, filename, last_modified
            FROM notes
            ORDER BY last_modified DESC
            LIMIT $count
            """;
        cmd.Parameters.AddWithValue("$count", count);
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string, long)>();
        while (r.Read())
            result.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return result;
    }

    /// <summary>全文検索（FTS5 trigram）。スコアは bm25 値（小さいほど関連度高）。</summary>
    public IEnumerable<(NoteEntry Note, double Score)> Search(string query, int limit = 20)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT n.relative_path, n.folder, n.filename, n.last_modified, n.indexed_at,
                   n.content, n.content_hash, bm25(notes_fts) AS score
            FROM notes n
            JOIN notes_fts ON notes_fts.rowid = n.id
            WHERE notes_fts MATCH $query
            ORDER BY score
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$query", Normalize(query));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var result = new List<(NoteEntry, double)>();
        while (r.Read())
            result.Add((ReadNoteEntry(r), r.GetDouble(7)));
        return result;
    }

    /// <summary>全文検索（FTS5 trigram）+ SQLite snippet 付き。パスとスニペットのみ返す。</summary>
    public IEnumerable<(string RelativePath, string Snippet)> SearchWithSnippets(string query, int limit = 20)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT n.relative_path,
                   snippet(notes_fts, 0, '【', '】', '…', 20) AS snip
            FROM notes n
            JOIN notes_fts ON notes_fts.rowid = n.id
            WHERE notes_fts MATCH $query
            ORDER BY bm25(notes_fts)
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$query", Normalize(query));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string)>();
        while (r.Read())
            result.Add((r.GetString(0), r.GetString(1)));
        return result;
    }

    // -------------------------------------------------------------------------
    // excluded_folders テーブル操作
    // -------------------------------------------------------------------------

    /// <summary>除外フォルダを追加する（既存の場合は note を更新）。</summary>
    public void AddExcludedFolder(string path, string? note = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO excluded_folders (path, note) VALUES ($path, $note)
            ON CONFLICT(path) DO UPDATE SET note = excluded.note
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$note", note ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>除外フォルダを削除する。</summary>
    public void RemoveExcludedFolder(string path)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM excluded_folders WHERE path = $path";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    /// <summary>除外フォルダ一覧を返す。</summary>
    public IEnumerable<ExcludedFolder> GetExcludedFolders()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, note FROM excluded_folders ORDER BY path";
        using var r = cmd.ExecuteReader();
        var result = new List<ExcludedFolder>();
        while (r.Read())
            result.Add(new ExcludedFolder(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        return result;
    }

    /// <summary>指定の相対パスが除外対象フォルダ配下かどうかを返す。</summary>
    public bool IsExcluded(string relativePath)
    {
        foreach (var e in GetExcludedFolders())
        {
            if (relativePath.Equals(e.Path, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(e.Path + "/",  StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(e.Path + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // FTS5 操作
    // -------------------------------------------------------------------------

    private void FtsDelete(long noteId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM notes_fts WHERE rowid = $id";
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.ExecuteNonQuery();
    }

    private void FtsInsert(long noteId, string content)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO notes_fts(rowid, content) VALUES($id, $content)";
        cmd.Parameters.AddWithValue("$id",      noteId);
        cmd.Parameters.AddWithValue("$content", Normalize(content));
        cmd.ExecuteNonQuery();
    }

    // -------------------------------------------------------------------------
    // サブテーブル操作
    // -------------------------------------------------------------------------

    private void DeleteRelated(long noteId)
    {
        foreach (var tbl in new[] { "note_meta", "note_headings", "note_aliases" })
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"DELETE FROM {tbl} WHERE note_id = $id";
            cmd.Parameters.AddWithValue("$id", noteId);
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertMeta(long noteId, Dictionary<string, List<string>> meta)
    {
        foreach (var (key, values) in meta)
            foreach (var val in values)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO note_meta(note_id, key, value) VALUES($id, $key, $val)";
                cmd.Parameters.AddWithValue("$id",  noteId);
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$val", val);
                cmd.ExecuteNonQuery();
            }
    }

    private void InsertAliases(long noteId, List<string> aliases)
    {
        foreach (var alias in aliases)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO note_aliases(note_id, alias) VALUES($id, $alias)";
            cmd.Parameters.AddWithValue("$id",    noteId);
            cmd.Parameters.AddWithValue("$alias", alias);
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertHeadings(long noteId, List<(int level, string text)> headings)
    {
        foreach (var (level, text) in headings)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO note_headings(note_id, level, text) VALUES($id, $level, $text)";
            cmd.Parameters.AddWithValue("$id",    noteId);
            cmd.Parameters.AddWithValue("$level", level);
            cmd.Parameters.AddWithValue("$text",  text);
            cmd.ExecuteNonQuery();
        }
    }

    // -------------------------------------------------------------------------
    // パース
    // -------------------------------------------------------------------------

    /// <summary>ノートのコンテンツを解析してメタ・エイリアス・見出しを返す。</summary>
    internal static (Dictionary<string, List<string>> meta, List<string> aliases, List<(int level, string text)> headings)
        ParseNote(string content)
    {
        var meta     = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var headings = new List<(int level, string text)>();
        string body  = content;

        // YAMLフロントマター抽出
        if (content.StartsWith("---\n") || content.StartsWith("---\r\n"))
        {
            int yamlStart = content.IndexOf('\n') + 1;
            int yamlEnd   = content.IndexOf("\n---", yamlStart);
            if (yamlEnd >= 0)
            {
                ParseYaml(content[yamlStart..yamlEnd], meta);
                body = content[(yamlEnd + 4)..].TrimStart('\r', '\n');
            }
        }

        // 見出し抽出（フロントマター外）
        foreach (var rawLine in body.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] != '#') continue;
            int level = 0;
            while (level < line.Length && line[level] == '#') level++;
            if (level > 6 || level >= line.Length || line[level] != ' ') continue;
            string heading = line[(level + 1)..].Trim();
            if (!string.IsNullOrEmpty(heading))
                headings.Add((level, heading));
        }

        var aliases = meta.TryGetValue("aliases", out var al) ? al : new List<string>();
        return (meta, aliases, headings);
    }

    private static void ParseYaml(string yaml, Dictionary<string, List<string>> result)
    {
        string? currentKey = null;

        foreach (var rawLine in yaml.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            // リスト項目（"  - value" または "- value"）
            if (Regex.IsMatch(line, @"^\s+-\s"))
            {
                if (currentKey == null) continue;
                string val = line.TrimStart().TrimStart('-').Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(val))
                    GetOrCreate(result, currentKey).Add(val);
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            string key      = line[..colon].Trim().ToLowerInvariant();
            string valueStr = line[(colon + 1)..].Trim();
            currentKey = key;

            // インライン配列: [a, b, c]
            if (valueStr.StartsWith('[') && valueStr.EndsWith(']'))
            {
                var items = valueStr[1..^1].Split(',')
                    .Select(s => s.Trim().Trim('"', '\''))
                    .Where(s => !string.IsNullOrEmpty(s));
                GetOrCreate(result, key).AddRange(items);
                continue;
            }

            if (!string.IsNullOrEmpty(valueStr))
                GetOrCreate(result, key).Add(valueStr.Trim('"', '\''));
            else
                GetOrCreate(result, key); // 次行にリスト値が続く場合の予約
        }
    }

    // -------------------------------------------------------------------------
    // ユーティリティ
    // -------------------------------------------------------------------------

    /// <summary>コンテンツの SHA256 ハッシュ（先頭16文字）を返す。</summary>
    public static string ComputeHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>検索用正規化: NFKC + ひらがな→カタカナ + 英字小文字化。</summary>
    public static string Normalize(string text)
    {
        var nfkc = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(nfkc.Length);
        foreach (char c in nfkc)
            sb.Append(c >= '\u3041' && c <= '\u3096' ? (char)(c + 0x60) : c);
        return sb.ToString().ToLowerInvariant();
    }

    private long? GetNoteId(string relativePath)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id FROM notes WHERE relative_path = $path";
        cmd.Parameters.AddWithValue("$path", relativePath);
        var result = cmd.ExecuteScalar();
        return result is long id ? id : null;
    }

    private static List<string> GetOrCreate(Dictionary<string, List<string>> d, string key)
    {
        if (!d.TryGetValue(key, out var list))
            d[key] = list = new List<string>();
        return list;
    }

    // -------------------------------------------------------------------------
    // ヘルパー
    // -------------------------------------------------------------------------

    private static void BindNoteEntry(SqliteCommand cmd, NoteEntry e)
    {
        cmd.Parameters.AddWithValue("$path",     e.RelativePath);
        cmd.Parameters.AddWithValue("$folder",   e.Folder       ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$filename", e.Filename);
        cmd.Parameters.AddWithValue("$lm",       e.LastModified);
        cmd.Parameters.AddWithValue("$ia",       e.IndexedAt);
        cmd.Parameters.AddWithValue("$content",  e.Content      ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$hash",     e.ContentHash  ?? (object)DBNull.Value);
    }

    private static NoteEntry ReadNoteEntry(SqliteDataReader r) => new(
        r.GetString(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.GetString(2),
        r.GetInt64(3),
        r.GetInt64(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6)
    );

    public void Dispose() => _db.Dispose();
}

// -------------------------------------------------------------------------
// データモデル
// -------------------------------------------------------------------------

public record NoteEntry(
    string  RelativePath,
    string? Folder,
    string  Filename,
    long    LastModified,
    long    IndexedAt,
    string? Content     = null,
    string? ContentHash = null
);

public record ExcludedFolder(
    string  Path,
    string? Note
);
