using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

public enum ImportResult { Added, Updated, Unchanged }

/// <summary>notes テーブルの 1 行（本文を含む）。Content は LF 正規化済み。</summary>
public sealed record NoteRecord(
    long    Id,
    string  RelativePath,
    string? Folder,
    string  Filename,
    string  Content,
    long    Version,
    long    FileMtime,      // ファイルの LastWriteTimeUtc.Ticks（取込時／書き戻し時）
    long    FileSize,       // ファイルのバイト数（取込時／書き戻し時）
    long    LastModified    // unix 秒（一覧・recent 用）
);

/// <summary>一覧用の軽量な行（本文を含まない）。</summary>
public sealed record NoteInfo(string RelativePath, string? Folder, string Filename, long Version, long LastModified, long FileSize);

public sealed record HistoryEntry(long Id, string Op, string? PrevContent, long? PrevVersion, long AfterVersion, long CreatedAt);

public sealed record InboxItem(string RelativePath, string? From, string? Status, string? Subject, long LastModified);

/// <summary>
/// Vault の SQLite ストア。2.0 では SQLite を正とし、md はここから書き戻す。
/// </summary>
public sealed class VaultStore : IDisposable
{
    private const int SchemaVersion = 2;
    private readonly SqliteConnection _db;

    public VaultStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}") { DefaultTimeout = 15 };
        _db.Open();
        ApplySchema();
        SeedDefaults();
    }

    // -------------------------------------------------------------------------
    // スキーマ
    // -------------------------------------------------------------------------

    private void ApplySchema()
    {
        using (var pragma = _db.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        long userVersion;
        using (var uv = _db.CreateCommand())
        {
            uv.CommandText = "PRAGMA user_version";
            userVersion = (long)uv.ExecuteScalar()!;
        }

        if (userVersion != SchemaVersion)
        {
            // 1.x のスキーマ（hash ベースの履歴など）とは互換がないので作り直す。起動時のフルスキャンで再取込される
            using var drop = _db.CreateCommand();
            drop.CommandText = """
                DROP TABLE IF EXISTS note_meta;
                DROP TABLE IF EXISTS note_headings;
                DROP TABLE IF EXISTS note_aliases;
                DROP TABLE IF EXISTS notes_fts;
                DROP TABLE IF EXISTS note_history;
                DROP TABLE IF EXISTS notes;
                DROP TABLE IF EXISTS excluded_folders;
                """;
            drop.ExecuteNonQuery();
        }

        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS notes (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                relative_path  TEXT    NOT NULL UNIQUE,
                folder         TEXT,
                filename       TEXT    NOT NULL,
                content        TEXT    NOT NULL,
                content_norm   TEXT    NOT NULL,
                version        INTEGER NOT NULL DEFAULT 1,
                file_mtime     INTEGER NOT NULL,
                file_size      INTEGER NOT NULL,
                last_modified  INTEGER NOT NULL,
                indexed_at     INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS excluded_folders (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                path  TEXT    NOT NULL UNIQUE,
                note  TEXT
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

            -- rollback 用の書き込み履歴（notes の削除後も残すため外部キーなし）
            CREATE TABLE IF NOT EXISTS note_history (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                relative_path  TEXT    NOT NULL,
                op             TEXT    NOT NULL,
                prev_content   TEXT,               -- NULL = 書き込み前はノートが存在しなかった
                prev_version   INTEGER,
                after_version  INTEGER NOT NULL,   -- 0 = 書き込み後はノートが存在しない（delete）
                created_at     INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_notes_folder        ON notes         (folder);
            CREATE INDEX IF NOT EXISTS idx_notes_last_modified ON notes         (last_modified);
            CREATE INDEX IF NOT EXISTS idx_note_meta_note_id   ON note_meta     (note_id);
            CREATE INDEX IF NOT EXISTS idx_note_meta_key       ON note_meta     (key, value);
            CREATE INDEX IF NOT EXISTS idx_headings_note_id    ON note_headings (note_id);
            CREATE INDEX IF NOT EXISTS idx_aliases_note_id     ON note_aliases  (note_id);
            CREATE INDEX IF NOT EXISTS idx_history_path        ON note_history  (relative_path, id);

            PRAGMA user_version = {SchemaVersion};
            """;
        cmd.ExecuteNonQuery();
    }

    private static readonly (string path, string note)[] DefaultExcludedFolders =
    [
        (".obsidian", "Obsidianシステムフォルダ"),
        (".trash",    "削除済みノート"),
    ];

    private void SeedDefaults()
    {
        using var check = _db.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM excluded_folders";
        if ((long)check.ExecuteScalar()! > 0) return;
        foreach (var (path, note) in DefaultExcludedFolders)
            AddExcludedFolder(path, note);
    }

    // -------------------------------------------------------------------------
    // 取込・保存
    // -------------------------------------------------------------------------

    /// <summary>
    /// md から取り込む。新規なら version 1 で追加、本文が変わっていれば version を 1 上げて更新、
    /// 同じならファイル情報（mtime・サイズ）だけ更新する。
    /// </summary>
    public (ImportResult Result, long Version) Import(string relativePath, string content, long fileMtime, long fileSize, long lastModified)
    {
        var existing = Get(relativePath);
        if (existing is null)
        {
            Upsert(relativePath, content, 1, fileMtime, fileSize, lastModified);
            return (ImportResult.Added, 1);
        }
        if (existing.Content == content)
        {
            UpdateFileInfo(existing.Id, fileMtime, fileSize, lastModified);
            return (ImportResult.Unchanged, existing.Version);
        }
        long v = existing.Version + 1;
        Upsert(relativePath, content, v, fileMtime, fileSize, lastModified);
        return (ImportResult.Updated, v);
    }

    /// <summary>
    /// MCPVault 自身の書き込み後に保存する。version は既存 +1（新規なら 1、forceVersion 指定時はその値）。
    /// </summary>
    public long Save(string relativePath, string content, long fileMtime, long fileSize, long lastModified, long? forceVersion = null)
    {
        var existing = Get(relativePath);
        long v = forceVersion ?? (existing is null ? 1 : existing.Version + 1);
        Upsert(relativePath, content, v, fileMtime, fileSize, lastModified);
        return v;
    }

    private void Upsert(string relativePath, string content, long version, long fileMtime, long fileSize, long lastModified)
    {
        string folder   = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";
        string filename = Path.GetFileName(relativePath);
        string norm     = Normalize(content);

        using var tx = _db.BeginTransaction();

        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO notes (relative_path, folder, filename, content, content_norm, version, file_mtime, file_size, last_modified, indexed_at)
                VALUES ($path, $folder, $filename, $content, $norm, $ver, $mtime, $size, $lm, $now)
                ON CONFLICT(relative_path) DO UPDATE SET
                    folder        = excluded.folder,
                    filename      = excluded.filename,
                    content       = excluded.content,
                    content_norm  = excluded.content_norm,
                    version       = excluded.version,
                    file_mtime    = excluded.file_mtime,
                    file_size     = excluded.file_size,
                    last_modified = excluded.last_modified,
                    indexed_at    = excluded.indexed_at
                """;
            cmd.Parameters.AddWithValue("$path",     relativePath);
            cmd.Parameters.AddWithValue("$folder",   folder.Length == 0 ? DBNull.Value : folder);
            cmd.Parameters.AddWithValue("$filename", filename);
            cmd.Parameters.AddWithValue("$content",  content);
            cmd.Parameters.AddWithValue("$norm",     norm);
            cmd.Parameters.AddWithValue("$ver",      version);
            cmd.Parameters.AddWithValue("$mtime",    fileMtime);
            cmd.Parameters.AddWithValue("$size",     fileSize);
            cmd.Parameters.AddWithValue("$lm",       lastModified);
            cmd.Parameters.AddWithValue("$now",      DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }

        long noteId = GetNoteId(relativePath, tx)!.Value;

        FtsDelete(noteId, tx);
        FtsInsert(noteId, norm, tx);

        DeleteRelated(noteId, tx);
        var (meta, aliases, headings) = ParseNote(content);
        InsertMeta(noteId, meta, tx);
        InsertAliases(noteId, aliases, tx);
        InsertHeadings(noteId, headings, tx);

        tx.Commit();
    }

    private void UpdateFileInfo(long id, long fileMtime, long fileSize, long lastModified)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE notes SET file_mtime = $mtime, file_size = $size, last_modified = $lm, indexed_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$mtime", fileMtime);
        cmd.Parameters.AddWithValue("$size",  fileSize);
        cmd.Parameters.AddWithValue("$lm",    lastModified);
        cmd.Parameters.AddWithValue("$now",   DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id",    id);
        cmd.ExecuteNonQuery();
    }

    public NoteRecord? Get(string relativePath)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, relative_path, folder, filename, content, version, file_mtime, file_size, last_modified
            FROM notes WHERE relative_path = $path
            """;
        cmd.Parameters.AddWithValue("$path", relativePath);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRecord(r) : null;
    }

    public void Delete(string relativePath)
    {
        using var tx = _db.BeginTransaction();
        long? noteId = GetNoteId(relativePath, tx);
        if (noteId.HasValue) FtsDelete(noteId.Value, tx);
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM notes WHERE relative_path = $path";
        cmd.Parameters.AddWithValue("$path", relativePath);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>ノートのパスを変更する（移動・改名。本文・version はそのまま）。</summary>
    public void RenamePath(string oldPath, string newPath, long fileMtime, long fileSize, long lastModified)
    {
        string folder   = Path.GetDirectoryName(newPath)?.Replace('\\', '/') ?? "";
        string filename = Path.GetFileName(newPath);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE notes SET relative_path = $new, folder = $folder, filename = $filename,
                             file_mtime = $mtime, file_size = $size, last_modified = $lm
            WHERE relative_path = $old
            """;
        cmd.Parameters.AddWithValue("$new",      newPath);
        cmd.Parameters.AddWithValue("$folder",   folder.Length == 0 ? DBNull.Value : folder);
        cmd.Parameters.AddWithValue("$filename", filename);
        cmd.Parameters.AddWithValue("$mtime",    fileMtime);
        cmd.Parameters.AddWithValue("$size",     fileSize);
        cmd.Parameters.AddWithValue("$lm",       lastModified);
        cmd.Parameters.AddWithValue("$old",      oldPath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>フォルダのリネーム／移動に伴い、配下ノートのパスを一括更新する。</summary>
    public int RenameFolder(string oldPath, string newPath)
    {
        string oldSlash = oldPath + "/";
        string newSlash = newPath + "/";
        int    startPos = oldSlash.Length + 1;   // SQLite SUBSTR は 1-indexed

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE notes
            SET relative_path = $newSlash || SUBSTR(relative_path, $startPos),
                folder = CASE
                    WHEN folder = $oldPath                                 THEN $newPath
                    WHEN SUBSTR(folder, 1, LENGTH($oldSlash)) = $oldSlash  THEN $newSlash || SUBSTR(folder, $startPos)
                    ELSE folder
                END
            WHERE SUBSTR(relative_path, 1, LENGTH($oldSlash)) = $oldSlash
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
        var keep  = existingPaths.ToHashSet(PathRules.Comparer);
        var stale = ListPaths([]).Where(p => !keep.Contains(p)).ToList();
        foreach (var p in stale) Delete(p);
        return stale.Count;
    }

    // -------------------------------------------------------------------------
    // 一覧
    // -------------------------------------------------------------------------

    public List<NoteInfo> ListInfos(string? folder, string[] scope)
    {
        using var cmd = _db.CreateCommand();
        string where = "1=1";
        if (folder != null)
        {
            where = "(folder = $folder OR SUBSTR(folder, 1, LENGTH($folderSlash)) = $folderSlash)";
            cmd.Parameters.AddWithValue("$folder",      folder);
            cmd.Parameters.AddWithValue("$folderSlash", folder + "/");
        }
        cmd.CommandText = $"""
            SELECT relative_path, folder, filename, version, last_modified, file_size
            FROM notes WHERE {where}{ScopeSql(cmd, scope, "relative_path")}
            ORDER BY relative_path
            """;
        using var r = cmd.ExecuteReader();
        var result = new List<NoteInfo>();
        while (r.Read())
            result.Add(new NoteInfo(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
        return result;
    }

    public List<string> ListPaths(string[] scope)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT relative_path FROM notes WHERE 1=1{ScopeSql(cmd, scope, "relative_path")} ORDER BY relative_path";
        using var r = cmd.ExecuteReader();
        var result = new List<string>();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    public int CountNotes(string[] scope)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM notes WHERE 1=1{ScopeSql(cmd, scope, "relative_path")}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<NoteInfo> Recent(int count, string[] scope)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            SELECT relative_path, folder, filename, version, last_modified, file_size
            FROM notes WHERE 1=1{ScopeSql(cmd, scope, "relative_path")}
            ORDER BY last_modified DESC
            LIMIT $count
            """;
        cmd.Parameters.AddWithValue("$count", count);
        using var r = cmd.ExecuteReader();
        var result = new List<NoteInfo>();
        while (r.Read())
            result.Add(new NoteInfo(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
        return result;
    }

    // scope（フォルダ相対パスの配列）を WHERE 句の枝にする。空なら制限なし
    private static string ScopeSql(SqliteCommand cmd, string[] scope, string col)
    {
        if (scope.Length == 0) return "";
        var parts = new List<string>();
        for (int i = 0; i < scope.Length; i++)
        {
            string prefix = scope[i] + "/";
            parts.Add($"SUBSTR({col}, 1, {prefix.Length}) = $scope{i}");
            cmd.Parameters.AddWithValue($"$scope{i}", prefix);
        }
        return " AND (" + string.Join(" OR ", parts) + ")";
    }

    // frontmatter 条件（key = value の AND）を WHERE 句の枝にする
    private static string FrontmatterSql(SqliteCommand cmd, Dictionary<string, string>? fm, string noteIdCol)
    {
        if (fm is null || fm.Count == 0) return "";
        var sb = new StringBuilder();
        int i = 0;
        foreach (var (key, value) in fm)
        {
            sb.Append($" AND EXISTS (SELECT 1 FROM note_meta fm{i} WHERE fm{i}.note_id = {noteIdCol} AND fm{i}.key = $fmk{i} AND fm{i}.value = $fmv{i} COLLATE NOCASE)");
            cmd.Parameters.AddWithValue($"$fmk{i}", key.ToLowerInvariant());
            cmd.Parameters.AddWithValue($"$fmv{i}", value);
            i++;
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // 全文検索
    // -------------------------------------------------------------------------

    private const int SnippetContext = 30;

    /// <summary>クエリを正規化し、空白で分割した語の配列を返す（空の語は除く）。</summary>
    public static string[] SplitQuery(string query) =>
        Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 全文検索。語がすべて3文字以上なら FTS5 trigram（bm25 順）、
    /// 2文字以下の語を含む場合は正規化済み本文（content_norm）の走査（出現回数順）で引く。
    /// 語が無く frontmatter 条件だけの場合は更新日時順に返す。scope・frontmatter はどの経路でも同じ条件で適用する。
    /// </summary>
    public List<(string RelativePath, string Snippet)> Search(string query, int limit, Dictionary<string, string>? frontmatter, string[] scope)
    {
        var terms = SplitQuery(query);
        if (terms.Length == 0)
            return (frontmatter is { Count: > 0 }) ? MetaOnlySearch(limit, frontmatter, scope) : [];
        return terms.Any(t => t.Length < 3)
            ? ScanSearch(terms, limit, frontmatter, scope)
            : FtsSearch(terms, limit, frontmatter, scope);
    }

    private List<(string, string)> FtsSearch(string[] terms, int limit, Dictionary<string, string>? fm, string[] scope)
    {
        // 各語を "…" で囲む（内部の " は二重化）。記号を含む語でも構文エラーにならず、複数語は暗黙 AND
        string match = string.Join(" ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));

        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            SELECT n.relative_path, n.content
            FROM notes n
            JOIN notes_fts ON notes_fts.rowid = n.id
            WHERE notes_fts MATCH $query{ScopeSql(cmd, scope, "n.relative_path")}{FrontmatterSql(cmd, fm, "n.id")}
            ORDER BY bm25(notes_fts)
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$query", match);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string)>();
        while (r.Read())
            result.Add((r.GetString(0), BuildSnippet(r.GetString(1), terms)));
        return result;
    }

    private List<(string, string)> ScanSearch(string[] terms, int limit, Dictionary<string, string>? fm, string[] scope)
    {
        string where = string.Join(" AND ", terms.Select((_, i) => $"instr(n.content_norm, $t{i}) > 0"));

        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            SELECT n.relative_path, n.content, n.content_norm, n.last_modified
            FROM notes n
            WHERE {where}{ScopeSql(cmd, scope, "n.relative_path")}{FrontmatterSql(cmd, fm, "n.id")}
            """;
        for (int i = 0; i < terms.Length; i++)
            cmd.Parameters.AddWithValue($"$t{i}", terms[i]);

        using var r = cmd.ExecuteReader();
        var hits = new List<(string Path, string Content, int Count, long LastModified)>();
        while (r.Read())
        {
            string norm = r.GetString(2);
            int count   = terms.Sum(t => CountOccurrences(norm, t));
            hits.Add((r.GetString(0), r.GetString(1), count, r.GetInt64(3)));
        }
        return hits
            .OrderByDescending(h => h.Count)
            .ThenByDescending(h => h.LastModified)
            .Take(limit)
            .Select(h => (h.Path, BuildSnippet(h.Content, terms)))
            .ToList();
    }

    private List<(string, string)> MetaOnlySearch(int limit, Dictionary<string, string> fm, string[] scope)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            SELECT n.relative_path, n.content
            FROM notes n
            WHERE 1=1{ScopeSql(cmd, scope, "n.relative_path")}{FrontmatterSql(cmd, fm, "n.id")}
            ORDER BY n.last_modified DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string)>();
        while (r.Read())
            result.Add((r.GetString(0), BuildSnippet(r.GetString(1), [])));
        return result;
    }

    private static int CountOccurrences(string text, string term)
    {
        int count = 0, pos = 0;
        while ((pos = text.IndexOf(term, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += term.Length;
        }
        return count;
    }

    /// <summary>
    /// 原文からスニペットを切り出す。原文の各文字（Rune）を個別に正規化して位置の対応表を作り、
    /// 正規化後のテキスト上で語を探して原文の範囲に戻す。見つかった語は【】で囲み、改行・連続空白は1つの空白にまとめる。
    /// </summary>
    public static string BuildSnippet(string content, string[] terms)
    {
        // 対応表: 正規化後テキストの各位置 → 原文上の開始位置
        // 結合文字（濁点・半角の濁点/半濁点など）は直前の文字とまとめて正規化し、全体正規化（ｶ+ﾞ→ガ）と一致させる
        var normSb   = new StringBuilder(content.Length);
        var rawStart = new List<int>(content.Length);
        var runes    = content.EnumerateRunes().ToList();
        int rawPos   = 0;
        for (int i = 0; i < runes.Count;)
        {
            int groupStart = rawPos;
            int groupLen   = runes[i].Utf16SequenceLength;
            int j = i + 1;
            while (j < runes.Count && IsCombining(runes[j]))
                groupLen += runes[j++].Utf16SequenceLength;

            string n = Normalize(content.Substring(groupStart, groupLen));
            normSb.Append(n);
            for (int k = 0; k < n.Length; k++) rawStart.Add(groupStart);
            rawPos += groupLen;
            i = j;
        }
        string norm = normSb.ToString();
        int RawEnd(int normIdxExclusive) => normIdxExclusive < rawStart.Count ? rawStart[normIdxExclusive] : content.Length;

        var matches = new List<(int Start, int End)>();
        foreach (var t in terms)
        {
            int pos = 0;
            while ((pos = norm.IndexOf(t, pos, StringComparison.Ordinal)) >= 0)
            {
                matches.Add((rawStart[pos], RawEnd(pos + t.Length)));
                pos += t.Length;
            }
        }

        if (matches.Count == 0)
        {
            int cut = Math.Min(content.Length, SnippetContext * 2);
            if (cut < content.Length && char.IsLowSurrogate(content[cut])) cut++;
            return Collapse(cut < content.Length ? content[..cut] + "…" : content);
        }

        matches.Sort();
        int winStart = Math.Max(0, matches[0].Start - SnippetContext);
        int winEnd   = Math.Min(content.Length, matches[0].End + SnippetContext);
        if (winStart > 0 && char.IsLowSurrogate(content[winStart])) winStart--;
        if (winEnd < content.Length && char.IsLowSurrogate(content[winEnd])) winEnd++;

        var sb = new StringBuilder();
        if (winStart > 0) sb.Append('…');
        int cursor = winStart;
        foreach (var (s, e) in matches)
        {
            if (s < cursor) continue;
            if (e > winEnd) break;
            sb.Append(content, cursor, s - cursor).Append('【').Append(content, s, e - s).Append('】');
            cursor = e;
        }
        sb.Append(content, cursor, winEnd - cursor);
        if (winEnd < content.Length) sb.Append('…');
        return Collapse(sb.ToString());
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static bool IsCombining(Rune r)
    {
        if (r.Value == 0xFF9E || r.Value == 0xFF9F) return true;
        var cat = Rune.GetUnicodeCategory(r);
        return cat is System.Globalization.UnicodeCategory.NonSpacingMark
                   or System.Globalization.UnicodeCategory.SpacingCombiningMark
                   or System.Globalization.UnicodeCategory.EnclosingMark;
    }

    // -------------------------------------------------------------------------
    // inbox
    // -------------------------------------------------------------------------

    /// <summary>
    /// 宛先が自分（frontmatter の to が一致）か、宛先なし（to が無く from がある）のメッセージで、
    /// status が done / archived / closed でないものを更新日時降順で返す。archive/ 配下は除く。
    /// </summary>
    public List<InboxItem> Inbox(string to, int limit, string[] scope)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            SELECT n.relative_path, n.last_modified,
                   (SELECT value FROM note_meta WHERE note_id = n.id AND key = 'from'   LIMIT 1),
                   (SELECT value FROM note_meta WHERE note_id = n.id AND key = 'status' LIMIT 1),
                   (SELECT text  FROM note_headings WHERE note_id = n.id ORDER BY id LIMIT 1)
            FROM notes n
            WHERE (
                    EXISTS (SELECT 1 FROM note_meta m WHERE m.note_id = n.id AND m.key = 'to' AND m.value = $to COLLATE NOCASE)
                 OR (    NOT EXISTS (SELECT 1 FROM note_meta m WHERE m.note_id = n.id AND m.key = 'to')
                     AND     EXISTS (SELECT 1 FROM note_meta m WHERE m.note_id = n.id AND m.key = 'from'))
                  )
              AND NOT EXISTS (SELECT 1 FROM note_meta m WHERE m.note_id = n.id AND m.key = 'status'
                              AND lower(m.value) IN ('done', 'archived', 'closed'))
              AND NOT (n.relative_path LIKE 'archive/%' OR n.relative_path LIKE '%/archive/%')
              {ScopeSql(cmd, scope, "n.relative_path")}
            ORDER BY n.last_modified DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$to",    to);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var result = new List<InboxItem>();
        while (r.Read())
            result.Add(new InboxItem(
                r.GetString(0),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt64(1)));
        return result;
    }

    // -------------------------------------------------------------------------
    // 履歴（rollback）
    // -------------------------------------------------------------------------

    private static readonly string HistoryPathCollate = PathRules.IgnoreCase ? " COLLATE NOCASE" : "";

    /// <summary>
    /// 書き込み履歴を追加し、同じパスの履歴が keep 件を超えた分は古い順に削除する。
    /// prevContent = null は書き込み前にノートが存在しなかったこと、afterVersion = 0 は書き込み後にノートが存在しないことを表す。
    /// </summary>
    public void AddHistory(string relativePath, string op, string? prevContent, long? prevVersion, long afterVersion, int keep = 20)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO note_history (relative_path, op, prev_content, prev_version, after_version, created_at)
            VALUES ($path, $op, $prev, $prevVer, $after, $now);
            DELETE FROM note_history
            WHERE relative_path = $path{HistoryPathCollate}
              AND id NOT IN (SELECT id FROM note_history WHERE relative_path = $path{HistoryPathCollate} ORDER BY id DESC LIMIT $keep);
            """;
        cmd.Parameters.AddWithValue("$path",    relativePath);
        cmd.Parameters.AddWithValue("$op",      op);
        cmd.Parameters.AddWithValue("$prev",    prevContent ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$prevVer", prevVersion ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$after",   afterVersion);
        cmd.Parameters.AddWithValue("$now",     DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$keep",    keep);
        cmd.ExecuteNonQuery();
    }

    public HistoryEntry? GetLatestHistory(string relativePath)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, op, prev_content, prev_version, after_version, created_at
            FROM note_history WHERE relative_path = $path{HistoryPathCollate}
            ORDER BY id DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$path", relativePath);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new HistoryEntry(
            r.GetInt64(0),
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetInt64(3),
            r.GetInt64(4),
            r.GetInt64(5));
    }

    public int CountHistory(string relativePath)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM note_history WHERE relative_path = $path{HistoryPathCollate}";
        cmd.Parameters.AddWithValue("$path", relativePath);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void DeleteHistory(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM note_history WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // -------------------------------------------------------------------------
    // excluded_folders
    // -------------------------------------------------------------------------

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

    public HashSet<string> GetExcludedFolders()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path FROM excluded_folders ORDER BY path";
        using var r = cmd.ExecuteReader();
        var result = new HashSet<string>(PathRules.Comparer);
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    // -------------------------------------------------------------------------
    // FTS5 / サブテーブル
    // -------------------------------------------------------------------------

    private void FtsDelete(long noteId, SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM notes_fts WHERE rowid = $id";
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.ExecuteNonQuery();
    }

    private void FtsInsert(long noteId, string normalizedContent, SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO notes_fts(rowid, content) VALUES($id, $content)";
        cmd.Parameters.AddWithValue("$id",      noteId);
        cmd.Parameters.AddWithValue("$content", normalizedContent);
        cmd.ExecuteNonQuery();
    }

    private void DeleteRelated(long noteId, SqliteTransaction tx)
    {
        foreach (var tbl in new[] { "note_meta", "note_headings", "note_aliases" })
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {tbl} WHERE note_id = $id";
            cmd.Parameters.AddWithValue("$id", noteId);
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertMeta(long noteId, Dictionary<string, List<string>> meta, SqliteTransaction tx)
    {
        foreach (var (key, values) in meta)
            foreach (var val in values)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO note_meta(note_id, key, value) VALUES($id, $key, $val)";
                cmd.Parameters.AddWithValue("$id",  noteId);
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$val", val);
                cmd.ExecuteNonQuery();
            }
    }

    private void InsertAliases(long noteId, List<string> aliases, SqliteTransaction tx)
    {
        foreach (var alias in aliases)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO note_aliases(note_id, alias) VALUES($id, $alias)";
            cmd.Parameters.AddWithValue("$id",    noteId);
            cmd.Parameters.AddWithValue("$alias", alias);
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertHeadings(long noteId, List<(int level, string text)> headings, SqliteTransaction tx)
    {
        foreach (var (level, text) in headings)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
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

            if (Regex.IsMatch(line, @"^\s*-\s"))
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
                GetOrCreate(result, key);
        }
    }

    // -------------------------------------------------------------------------
    // ユーティリティ
    // -------------------------------------------------------------------------

    /// <summary>検索用正規化: NFKC + ひらがな→カタカナ + 英字小文字化。</summary>
    public static string Normalize(string text)
    {
        var nfkc = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(nfkc.Length);
        foreach (char c in nfkc)
            sb.Append(c >= 'ぁ' && c <= 'ゖ' ? (char)(c + 0x60) : c);
        return sb.ToString().ToLowerInvariant();
    }

    private long? GetNoteId(string relativePath, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
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

    private static NoteRecord ReadRecord(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetInt64(5),
        r.GetInt64(6),
        r.GetInt64(7),
        r.GetInt64(8));

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// DB ファイルを -wal / -shm ごと削除する。接続プールに残った接続を先に閉じる。
    /// </summary>
    public static void DeleteDatabase(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>ファイルシステムのパス比較ルール（Windows / macOS は大文字小文字を区別しない、Linux は区別する）。</summary>
public static class PathRules
{
    public static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public static readonly StringComparison Comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static readonly StringComparer   Comparer   = IgnoreCase ? StringComparer.OrdinalIgnoreCase   : StringComparer.Ordinal;
}
