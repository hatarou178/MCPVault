using System.Text;
using System.Text.RegularExpressions;

/// <summary>ツール実行中のエラー。メッセージがそのまま isError 応答になる。</summary>
public sealed class ToolException(string message) : Exception(message);

public sealed record NoteView(string Path, string Content, long Version, long LastModified, long Size);

/// <summary>書き込み系ツールの結果。After が null ならノートは存在しなくなった（rollback で作成を取り消した場合）。</summary>
public sealed record WriteOutcome(string Path, string Message, string? Before, string? After, long Version);

/// <summary>
/// target（main / guest / kcell）ごとの操作の実体。ツール側はこのインターフェースだけを見る。
/// </summary>
public interface INoteTarget
{
    string Name { get; }
    bool   ReadOnly { get; }

    NoteView Read(string path);
    string   List(string? folder);
    string   Recent(int count);
    string   Search(string query, int limit, Dictionary<string, string>? frontmatter);
    string   Inbox(string to, int limit);

    WriteOutcome Write(string? path, string content, bool overwrite, long? ifVersion);
    WriteOutcome Edit(string path, List<(string Old, string New)> edits, bool replaceAll, long? ifVersion);
    WriteOutcome Append(string? path, string content, bool journal);

    string Move(string source, string destination);
    string Delete(string path);
    string Folder(string op, string path, string? destination);

    WriteOutcome Rollback(string path, bool force);
    string       Status(string path);

    /// <summary>保存先（md / DB）を読み直して DB の内容と一致するか確認する。null なら一致。</summary>
    string? Verify(string path);
}

// =============================================================================
// 取込（md → SQLite）とファイル正規化
// =============================================================================

public static class Importer
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>正規形（BOM なし・LF）に揃える。DB に入れる本文と AI から受け取る本文の両方に使う。</summary>
    public static string NormalizeText(string s)
    {
        if (s.Length > 0 && s[0] == '﻿') s = s[1..];
        if (s.Contains('\r')) s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        return s;
    }

    /// <summary>md へ書き戻すときの形（CRLF）にする。引数は LF 正規化済みであること。</summary>
    public static string ToDisk(string normalized) => normalized.Replace("\n", "\r\n");

    public static string ReadFileNormalized(string fullPath) =>
        NormalizeText(File.ReadAllText(fullPath, Encoding.UTF8));

    public static string ToRelative(string vaultPath, string fullPath) =>
        Path.GetRelativePath(vaultPath, fullPath).Replace('\\', '/');

    public static long ToUnix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    /// <summary>1 ファイルを取り込む。ファイルが無ければ DB から削除する。</summary>
    public static (ImportResult Result, NoteRecord? Record) ImportFile(VaultStore store, string vaultPath, string fullPath)
    {
        string rel = ToRelative(vaultPath, fullPath);
        var fi = new FileInfo(fullPath);
        if (!fi.Exists)
        {
            store.Delete(rel);
            return (ImportResult.Unchanged, null);
        }
        string content = ReadFileNormalized(fullPath);
        var (res, _) = store.Import(rel, content, fi.LastWriteTimeUtc.Ticks, fi.Length, ToUnix(fi.LastWriteTimeUtc));
        return (res, store.Get(rel));
    }

    /// <summary>DB の記録とファイルの mtime・サイズが一致しているか。</summary>
    public static bool IsFresh(NoteRecord rec, FileInfo fi) =>
        fi.Exists && fi.LastWriteTimeUtc.Ticks == rec.FileMtime && fi.Length == rec.FileSize;

    // ---- 除外判定 ----

    public static bool IsExcludedFolder(string folder, HashSet<string> excluded, AppConfig cfg)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        foreach (var e in excluded)
            if (folder.Equals(e, PathRules.Comparison) || folder.StartsWith(e + "/", PathRules.Comparison))
                return true;
        foreach (var seg in folder.Split('/'))
        {
            if (cfg.ExcludeDotFolders        && seg.StartsWith('.'))  return true;
            if (cfg.ExcludeUnderscoreFolders && seg.StartsWith('_'))  return true;
            if (cfg.ExcludeIdeFolders        && IsIdeFolderName(seg)) return true;
        }
        return false;
    }

    private static bool IsIdeFolderName(string name) =>
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".git",         StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".vs",          StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".vscode",      StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".idea",        StringComparison.OrdinalIgnoreCase) ||
        name.Equals("__pycache__",  StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".gradle",      StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".mvn",         StringComparison.OrdinalIgnoreCase);

    public static bool HasNoCrawl(string dirPath) => File.Exists(Path.Combine(dirPath, "_NoCrawl"));

    /// <summary>ファイルのフルパスから Vault ルートまでの祖先に _NoCrawl があるか。</summary>
    public static bool IsUnderNoCrawl(string fullFilePath, string vaultRoot)
    {
        string dir       = Path.GetDirectoryName(fullFilePath)!;
        string vaultFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultRoot));
        while (true)
        {
            if (HasNoCrawl(dir)) return true;
            if (dir.Equals(vaultFull, PathRules.Comparison)) break;
            string? parent = Path.GetDirectoryName(dir);
            if (parent == null || parent.Equals(dir, PathRules.Comparison)) break;
            dir = parent;
        }
        return false;
    }

    /// <summary>Vault 全体をスキャンして取り込む（起動時・guest_open 時にバックグラウンドで実行）。</summary>
    public static void FullScan(string vaultPath, string dbPath, AppConfig cfg, string[] extraExcluded, Action<string> log)
    {
        string logDir       = Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", ".log");
        string indexLogPath = Path.Combine(logDir, $"index_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        try
        {
            Directory.CreateDirectory(logDir);
            using var indexLog = new StreamWriter(indexLogPath, append: false, Utf8NoBom) { AutoFlush = true };

            void ILog(string msg)
            {
                indexLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
                log($"[IDX] {msg}");
            }

            ILog($"=== Vault import start: {vaultPath} ===");
            ILog($"    DB: {dbPath}");

            using var store = new VaultStore(dbPath);

            foreach (var folder in extraExcluded)
                store.AddExcludedFolder(folder, "config/env");

            var excludedSet = store.GetExcludedFolders();
            ILog($"    Excluded: {string.Join(", ", excludedSet)}");

            var noCrawlDirs = Directory.EnumerateDirectories(vaultPath, "*", SearchOption.AllDirectories)
                .Where(HasNoCrawl)
                .Select(d => ToRelative(vaultPath, d))
                .ToHashSet(PathRules.Comparer);
            if (HasNoCrawl(vaultPath))
            {
                ILog("WARN: Vault ルートに _NoCrawl があります。全ノートが除外されます。");
                noCrawlDirs.Add("");
            }
            if (noCrawlDirs.Count > 0)
                ILog($"    NoCrawl: {string.Join(", ", noCrawlDirs)}");

            bool IsNoCrawled(string folder) =>
                noCrawlDirs.Any(nc => nc.Length == 0 ||
                    folder.Equals(nc, PathRules.Comparison) ||
                    folder.StartsWith(nc + "/", PathRules.Comparison));

            int added = 0, updated = 0, unchanged = 0, skipped = 0;
            var indexedPaths = new HashSet<string>(PathRules.Comparer);

            foreach (var file in Directory.EnumerateFiles(vaultPath, "*.md", SearchOption.AllDirectories))
            {
                string relative = ToRelative(vaultPath, file);
                string folder   = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";

                if (IsNoCrawled(folder)) { skipped++; continue; }
                if (IsExcludedFolder(folder, excludedSet, cfg)) { skipped++; ILog($"  SKP {relative}"); continue; }

                indexedPaths.Add(relative);
                try
                {
                    var (result, rec) = ImportFile(store, vaultPath, file);
                    string sizeLabel = rec != null ? $" {Fmt.Size(rec.FileSize)} v{rec.Version}" : "";
                    switch (result)
                    {
                        case ImportResult.Added:     added++;     ILog($"  ADD {relative}{sizeLabel}"); break;
                        case ImportResult.Updated:   updated++;   ILog($"  UPD {relative}{sizeLabel}"); break;
                        case ImportResult.Unchanged: unchanged++; break;
                    }
                }
                catch (Exception ex) { ILog($"  WARN: import failed {relative}: {ex.Message}"); }
            }

            int purged = store.Purge(indexedPaths);
            if (purged > 0) ILog($"  DEL {purged} stale record(s) purged");

            ILog($"=== Done: added={added} updated={updated} unchanged={unchanged} skipped={skipped} purged={purged} ===");
        }
        catch (Exception ex)
        {
            log($"[IDX] ERROR: {ex.Message}");
        }
    }
}

// =============================================================================
// 表示用フォーマット
// =============================================================================

public static class Fmt
{
    public static string Size(long n) => n switch
    {
        < 1_024         => $"{n}B",
        < 1_024 * 1_024 => $"{n / 1_024.0:F1}KB",
        _               => $"{n / (1_024.0 * 1_024):F1}MB",
    };

    public static string Dt(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public static string DtTicks(long utcTicks) => new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");

    /// <summary>append / kcell append 共通: 末尾に付け足す文字列を作る。</summary>
    public static string AppendEntry(string src, string content, bool journal)
    {
        string entry = journal ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}" : content;
        if (src.Length > 0 && !src.EndsWith('\n'))
            entry = "\n" + entry;
        if (journal && !entry.EndsWith('\n'))
            entry += "\n";
        return entry;
    }

    /// <summary>edit 共通: old → new の置換を全ペア適用する。失敗時は理由を返す（何も適用しない）。</summary>
    public static string? ApplyEdits(string src, List<(string Old, string New)> edits, bool replaceAll, int maxPairs,
        out string result, out int totalCount)
    {
        result = src;
        totalCount = 0;
        if (edits.Count == 0) return "edits が空です。old_str / new_str のペアを 1 つ以上指定してください。";
        if (edits.Count > maxPairs)
            return $"修正箇所が多すぎます（{edits.Count} ペア、上限 {maxPairs}）。全文を write で置き換えてください。";

        string cur = src;
        for (int i = 0; i < edits.Count; i++)
        {
            var (oldText, newText) = edits[i];
            if (string.IsNullOrEmpty(oldText)) return $"edits[{i}]: old_str が空です。";
            int count = CountOccurrences(cur, oldText);
            if (count == 0)
                return $"edits[{i}]: old_str が見つかりませんでした: 「{Head(oldText, 40)}」";
            if (count > 1 && !replaceAll)
                return $"edits[{i}]: old_str が {count} 箇所に一致しました。一意になるよう前後の文脈を含めるか、replace_all=true を指定してください: 「{Head(oldText, 40)}」";
            cur = cur.Replace(oldText, newText);
            totalCount += count;
        }
        result = cur;
        return null;
    }

    public static int CountOccurrences(string text, string term)
    {
        int count = 0, pos = 0;
        while ((pos = text.IndexOf(term, pos, StringComparison.Ordinal)) >= 0) { count++; pos += term.Length; }
        return count;
    }

    public static string Head(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

// =============================================================================
// Vault（main / guest）
// =============================================================================

public sealed class VaultTarget : INoteTarget
{
    private readonly string    _vaultPath;
    private readonly string    _dbPath;
    private readonly string[]  _scope;
    private readonly AppConfig _cfg;

    public string Name     { get; }
    public bool   ReadOnly { get; }
    public string VaultPath => _vaultPath;

    public VaultTarget(string name, string vaultPath, string dbPath, bool readOnly, string[] scope, AppConfig cfg)
    {
        Name       = name;
        _vaultPath = vaultPath;
        _dbPath    = dbPath;
        ReadOnly   = readOnly;
        _scope     = scope;
        _cfg       = cfg;
    }

    private VaultStore Open() => new(_dbPath);

    // ---- パス検証 -------------------------------------------------------------

    public static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    /// <summary>相対パスをフルパスに展開し、Vault 配下でなければ例外。</summary>
    private string Resolve(string rel)
    {
        string full      = Path.GetFullPath(Path.Combine(_vaultPath, rel.Replace('/', Path.DirectorySeparatorChar)));
        string vaultFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_vaultPath));
        if (!full.StartsWith(vaultFull + Path.DirectorySeparatorChar, PathRules.Comparison))
            throw new ToolException($"Vault 外へのアクセスは禁止されています（target: {Name}）: {rel}");
        return full;
    }

    private bool InScope(string rel, bool allowEqual)
    {
        if (_scope.Length == 0) return true;
        foreach (var s in _scope)
        {
            if (rel.StartsWith(s + "/", PathRules.Comparison)) return true;
            if (allowEqual && rel.Equals(s, PathRules.Comparison)) return true;
        }
        return false;
    }

    private void RequireInScope(string rel, bool isFolder = false)
    {
        if (InScope(rel, isFolder)) return;
        throw new ToolException(
            $"scope の範囲外です（target: {Name}、scope: {string.Join(", ", _scope)}）: {rel}\n" +
            "scope は mcp_config.json で人間が決めるもので、AI からは変更できません。");
    }

    private void RequireWritable()
    {
        if (!ReadOnly) return;
        throw new ToolException(
            $"{Name} Vault は read_only で開かれているため書き込めません。\n" +
            "書き込みが必要なら、人間が mcp_config.json の guest.mode を read_write に変更します（次の呼び出しから有効）。AI からは変更できません。");
    }

    private static void RequireMd(string rel)
    {
        if (!rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new ToolException($".md 以外のファイルへの書き込みは禁止されています: {rel}");
    }

    private string RequireNotePath(string? path, out string full)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ToolException("path が指定されていません。");
        string rel = Norm(path);
        full = Resolve(rel);
        RequireInScope(rel);
        return rel;
    }

    // ---- 取込・鮮度 -------------------------------------------------------------

    /// <summary>
    /// DB の記録とファイルを突き合わせ、ファイルが変わっていれば取り込み直して最新の記録を返す。
    /// ファイルが無ければ DB からも消して null を返す。
    /// </summary>
    private NoteRecord? EnsureFresh(VaultStore store, string rel, string full, NoteRecord? rec)
    {
        var fi = new FileInfo(full);
        if (!fi.Exists)
        {
            if (rec != null) store.Delete(rel);
            return null;
        }
        if (rec != null && Importer.IsFresh(rec, fi)) return rec;
        return Importer.ImportFile(store, _vaultPath, full).Record;
    }

    private NoteRecord? Fresh(VaultStore store, string rel, string full) => EnsureFresh(store, rel, full, store.Get(rel));

    /// <summary>md に書き戻し、DB を更新し、履歴を残す。戻り値は新しい version。</summary>
    private long WriteBack(VaultStore store, string rel, string full, string content, string op, NoteRecord? prev, long? forceVersion = null)
    {
        // 書き戻し直前の再確認: 取込時と違えば書かずにエラー（直前に EnsureFresh を通していれば通常は一致する）
        var before = new FileInfo(full);
        if (prev != null && before.Exists && !Importer.IsFresh(prev, before))
            throw new ToolException($"書き戻し直前に外部での変更を検出したため書き込みを中止しました: {rel}。read で取り込み直してから再試行してください。");
        if (prev == null && before.Exists)
            throw new ToolException($"書き戻し直前にファイルが外部で作成されたため書き込みを中止しました: {rel}。read で取り込んでから再試行してください。");

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, Importer.ToDisk(content), Importer.Utf8NoBom);
        var fi = new FileInfo(full);
        long v = store.Save(rel, content, fi.LastWriteTimeUtc.Ticks, fi.Length, Importer.ToUnix(fi.LastWriteTimeUtc), forceVersion);
        store.AddHistory(rel, op, prev?.Content, prev?.Version, v);
        return v;
    }

    private string NewMemoPath()
    {
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        string rel   = $"memo/memo{stamp}.md";
        for (int i = 2; File.Exists(Resolve(rel)); i++)
            rel = $"memo/memo{stamp}_{i}.md";
        return rel;
    }

    // ---- 読み取り -------------------------------------------------------------

    public NoteView Read(string path)
    {
        string rel = RequireNotePath(path, out string full);
        if (!rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            // md 以外は DB を通さずそのまま読む
            if (!File.Exists(full)) throw new ToolException($"ノートが見つかりません: {rel}");
            string raw = Importer.ReadFileNormalized(full);
            var fi = new FileInfo(full);
            return new NoteView(rel, raw, 0, Importer.ToUnix(fi.LastWriteTimeUtc), fi.Length);
        }
        using var store = Open();
        var rec = Fresh(store, rel, full) ?? throw new ToolException($"ノートが見つかりません: {rel}");
        return new NoteView(rel, rec.Content, rec.Version, rec.LastModified, rec.FileSize);
    }

    public string List(string? folder)
    {
        string? f = string.IsNullOrWhiteSpace(folder) ? null : Norm(folder).TrimEnd('/');
        if (f != null) { Resolve(f); RequireInScope(f, isFolder: true); }

        using var store = Open();
        var infos = store.ListInfos(f, _scope);
        if (infos.Count == 0)
            return f != null ? $"フォルダ「{f}」にノートはありません。" : "ノートがありません。";

        var sb = new StringBuilder();
        sb.AppendLine(f != null ? $"{f}/ のノート {infos.Count}件:" : $"全ノート {infos.Count}件:");
        var printed = new HashSet<string>(PathRules.Comparer);
        int baseDepth = f == null ? 0 : f.Split('/').Length;
        foreach (var n in infos.OrderBy(i => i.Folder ?? "", PathRules.Comparer).ThenBy(i => i.Filename, PathRules.Comparer))
        {
            string folderPath = n.Folder ?? "";
            int depth = 0;
            if (folderPath.Length > 0)
            {
                var segs = folderPath.Split('/');
                for (int i = baseDepth; i < segs.Length; i++)
                {
                    string prefix = string.Join('/', segs.Take(i + 1));
                    if (printed.Add(prefix))
                        sb.AppendLine(new string(' ', (i - baseDepth) * 2) + segs[i] + "/");
                }
                depth = Math.Max(0, segs.Length - baseDepth);
            }
            sb.AppendLine($"{new string(' ', depth * 2)}{n.Filename}  ({Fmt.Dt(n.LastModified)}, v{n.Version})");
        }
        return sb.ToString().TrimEnd();
    }

    public string Recent(int count)
    {
        using var store = Open();
        var rows = store.Recent(Math.Clamp(count, 1, 100), _scope);
        if (rows.Count == 0) return "ノートがありません。";
        var sb = new StringBuilder();
        sb.AppendLine($"最近更新された {rows.Count}件のノート:");
        int i = 1;
        foreach (var n in rows)
            sb.AppendLine($"{i++}. {n.RelativePath}  ({Fmt.Dt(n.LastModified)}, v{n.Version})");
        return sb.ToString().TrimEnd();
    }

    public string Search(string query, int limit, Dictionary<string, string>? frontmatter)
    {
        bool hasTerms = VaultStore.SplitQuery(query ?? "").Length > 0;
        bool hasMeta  = frontmatter is { Count: > 0 };
        if (!hasTerms && !hasMeta) throw new ToolException("query が空です（frontmatter 条件だけで検索する場合は frontmatter を指定してください）。");

        using var store = Open();
        var results = store.Search(query ?? "", Math.Clamp(limit, 1, 100), frontmatter, _scope);
        string label = hasTerms ? $"「{query}」" : "frontmatter 条件";
        if (results.Count == 0) return $"{label}に一致するノートは見つかりませんでした。";

        var sb = new StringBuilder();
        sb.AppendLine($"{label}の検索結果 {results.Count}件:");
        int i = 1;
        foreach (var (path, snippet) in results)
            sb.AppendLine($"{i++}. {path}\n   {snippet}");
        return sb.ToString().TrimEnd();
    }

    public string Inbox(string to, int limit)
    {
        if (string.IsNullOrWhiteSpace(to)) throw new ToolException("to（自分の nickname）が指定されていません。");
        using var store = Open();
        var items = store.Inbox(to.Trim(), Math.Clamp(limit, 1, 100), _scope);
        if (items.Count == 0) return $"「{to}」宛の未処理メッセージはありません。";
        var sb = new StringBuilder();
        sb.AppendLine($"「{to}」宛の未処理メッセージ {items.Count}件:");
        int i = 1;
        foreach (var m in items)
        {
            string from   = m.From   != null ? $" from: {m.From}" : "";
            string status = m.Status != null ? $" status: {m.Status}" : "";
            string subj   = m.Subject != null ? $"\n   {m.Subject}" : "";
            sb.AppendLine($"{i++}. {m.RelativePath}  ({Fmt.Dt(m.LastModified)}){from}{status}{subj}");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- 書き込み -------------------------------------------------------------

    public WriteOutcome Write(string? path, string content, bool overwrite, long? ifVersion)
    {
        RequireWritable();
        content = Importer.NormalizeText(content);

        string rel = string.IsNullOrWhiteSpace(path) ? NewMemoPath() : Norm(path);
        RequireMd(rel);
        string full = Resolve(rel);
        RequireInScope(rel);

        using var store = Open();
        var rec = Fresh(store, rel, full);
        if (rec != null)
        {
            if (!overwrite || ifVersion is null)
                throw new ToolException(
                    $"ノートがすでに存在します: {rel}（version {rec.Version}）。\n" +
                    $"全文を置き換えるには overwrite=true と if_version={rec.Version} の両方を指定してください。一部の修正は edit、末尾への追加は append を使ってください。");
            if (ifVersion != rec.Version)
                throw new ToolException(VersionMismatch(rel, ifVersion.Value, rec));
        }
        long v = WriteBack(store, rel, full, content, rec == null ? "create" : "write", rec);
        return new WriteOutcome(rel, rec == null ? "作成しました" : "上書きしました", rec?.Content, content, v);
    }

    public WriteOutcome Edit(string path, List<(string Old, string New)> edits, bool replaceAll, long? ifVersion)
    {
        RequireWritable();
        string rel = RequireNotePath(path, out string full);
        RequireMd(rel);

        using var store = Open();
        var rec = Fresh(store, rel, full) ?? throw new ToolException($"ノートが存在しません: {rel}");

        if (ifVersion is { } iv && iv != rec.Version)
            throw new ToolException(WithFull(VersionMismatch(rel, iv, rec), rec));

        var norm = edits.Select(e => (Importer.NormalizeText(e.Old), Importer.NormalizeText(e.New))).ToList();
        string? err = Fmt.ApplyEdits(rec.Content, norm, replaceAll, _cfg.EditMaxPairs, out string written, out int count);
        if (err != null) throw new ToolException(WithFull(err, rec));
        if (written == rec.Content) throw new ToolException($"変更がありません（old_str と new_str が同じです）: {rel}");

        long v = WriteBack(store, rel, full, written, "edit", rec);
        return new WriteOutcome(rel, $"置換しました ({count}箇所)", rec.Content, written, v);
    }

    public WriteOutcome Append(string? path, string content, bool journal)
    {
        RequireWritable();
        content = Importer.NormalizeText(content);

        string rel = string.IsNullOrWhiteSpace(path) ? NewMemoPath() : Norm(path);
        RequireMd(rel);
        string full = Resolve(rel);
        RequireInScope(rel);

        using var store = Open();
        var rec = Fresh(store, rel, full);
        string src     = rec?.Content ?? "";
        string written = src + Fmt.AppendEntry(src, content, journal);
        long v = WriteBack(store, rel, full, written, "append", rec);
        string msg = rec == null ? "作成して追記しました" : journal ? "ジャーナル追記しました" : "追記しました";
        return new WriteOutcome(rel, msg, rec?.Content, written, v);
    }

    private static string VersionMismatch(string rel, long given, NoteRecord rec) =>
        $"version が一致しません: {rel} は現在 version {rec.Version} です（指定: {given}）。内容を確認して if_version={rec.Version} で再試行してください。";

    private static string WithFull(string message, NoteRecord rec) =>
        $"{message}\n\n--- 現在の内容 ({rec.RelativePath}, version {rec.Version}) ---\n{rec.Content}";

    // ---- 管理 -------------------------------------------------------------

    public string Move(string source, string destination)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
            throw new ToolException("source と destination の両方が必要です。");
        string src = Norm(source), dst = Norm(destination);
        RequireMd(dst);
        string fullSrc = Resolve(src), fullDst = Resolve(dst);
        RequireInScope(src);
        RequireInScope(dst);

        if (!File.Exists(fullSrc)) throw new ToolException($"移動元が見つかりません: {src}");
        if (File.Exists(fullDst))  throw new ToolException($"移動先がすでに存在します: {dst}");

        using var store = Open();
        var rec = Fresh(store, src, fullSrc);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDst)!);
        File.Move(fullSrc, fullDst);
        var fi = new FileInfo(fullDst);
        if (rec != null)
            store.RenamePath(src, dst, fi.LastWriteTimeUtc.Ticks, fi.Length, Importer.ToUnix(fi.LastWriteTimeUtc));
        else
            Importer.ImportFile(store, _vaultPath, fullDst);

        int links = UpdateWikiLinksForNote(store, src, dst);
        return $"移動しました: {src} → {dst}" + (links > 0 ? $"（リンク修正: {links}件）" : "");
    }

    public string Delete(string path)
    {
        RequireWritable();
        if (!_cfg.AllowDelete)
            throw new ToolException(
                "削除権限が OFF のため delete は使えません。不要になったノートは archive/ へ move してください。\n" +
                "削除を許可するには、人間が mcp_config.json の permissions.allow_delete を true にします。AI からは変更できません。");

        string rel = RequireNotePath(path, out string full);
        using var store = Open();
        bool isMd = rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var rec = isMd ? Fresh(store, rel, full) : null;
        if (!File.Exists(full)) throw new ToolException($"ノートが見つかりません: {rel}");

        File.Delete(full);
        store.Delete(rel);
        if (rec != null) store.AddHistory(rel, "delete", rec.Content, rec.Version, 0);
        return $"削除しました: {rel}" + (rec != null ? "（rollback で復元できます）" : "");
    }

    public string Folder(string op, string path, string? destination)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(path)) throw new ToolException("path が指定されていません。");
        string rel  = Norm(path).TrimEnd('/');
        string full = Resolve(rel);
        RequireInScope(rel, isFolder: true);

        switch (op?.ToLowerInvariant())
        {
            case "create":
                if (Directory.Exists(full)) throw new ToolException($"フォルダがすでに存在します: {rel}");
                Directory.CreateDirectory(full);
                return $"フォルダを作成しました: {rel}";

            case "rename":
            {
                if (string.IsNullOrWhiteSpace(destination)) throw new ToolException("rename には destination が必要です。");
                string dst = Norm(destination).TrimEnd('/');
                string fullDst = Resolve(dst);
                RequireInScope(dst, isFolder: true);
                if (!Directory.Exists(full)) throw new ToolException($"フォルダが見つかりません: {rel}");
                if (Directory.Exists(fullDst) || File.Exists(fullDst)) throw new ToolException($"移動先がすでに存在します: {dst}");

                Directory.CreateDirectory(Path.GetDirectoryName(fullDst)!);
                Directory.Move(full, fullDst);
                using var store = Open();
                int updated = store.RenameFolder(rel, dst);
                int links   = UpdateWikiLinksForFolder(store, rel, dst);
                return $"フォルダを移動しました: {rel} → {dst}（インデックス更新: {updated}件" + (links > 0 ? $"、リンク修正: {links}件" : "") + "）";
            }

            case "delete":
                if (!_cfg.AllowFolderDelete)
                    throw new ToolException(
                        "フォルダ削除の権限が OFF です。人間が mcp_config.json の permissions.allow_folder_delete を true にすると使えます。AI からは変更できません。");
                if (!Directory.Exists(full)) throw new ToolException($"フォルダが見つかりません: {rel}");
                if (Directory.EnumerateFileSystemEntries(full).Any()) throw new ToolException($"フォルダが空ではありません: {rel}");
                Directory.Delete(full);
                return $"空フォルダを削除しました: {rel}";

            default:
                throw new ToolException($"op は create / rename / delete のいずれかです: {op}");
        }
    }

    // ---- 復元・確認 -------------------------------------------------------------

    public WriteOutcome Rollback(string path, bool force)
    {
        RequireWritable();
        string rel = RequireNotePath(path, out string full);
        RequireMd(rel);

        using var store = Open();
        var hist = store.GetLatestHistory(rel) ?? throw new ToolException($"ロールバックできる履歴がありません: {rel}");
        var rec  = Fresh(store, rel, full);
        long cur = rec?.Version ?? 0;

        if (cur != hist.AfterVersion && !force)
            throw new ToolException(
                $"{hist.Op} の後にノートが変更されています（現在 version {cur}、履歴の直後は version {hist.AfterVersion}）。" +
                $"force=true で強制的に戻せますが、その後の変更は失われます: {rel}");

        string? after;
        long v;
        if (hist.PrevContent is null)
        {
            if (File.Exists(full)) File.Delete(full);
            store.Delete(rel);
            after = null;
            v = 0;
        }
        else
        {
            // 書き戻し前の鮮度確認は force 時にはスキップする（外部変更を上書きする意図）
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Importer.ToDisk(hist.PrevContent), Importer.Utf8NoBom);
            var fi = new FileInfo(full);
            long? forceVersion = rec == null ? (hist.PrevVersion ?? 0) + 1 : null;
            v = store.Save(rel, hist.PrevContent, fi.LastWriteTimeUtc.Ticks, fi.Length, Importer.ToUnix(fi.LastWriteTimeUtc), forceVersion);
            after = hist.PrevContent;
        }
        store.DeleteHistory(hist.Id);

        string msg = $"ロールバックしました（{hist.Op} を取り消し、残り履歴 {store.CountHistory(rel)} 件）";
        if (after is null) msg += "。書き込み前はノートが存在しなかったため削除しました";
        return new WriteOutcome(rel, msg, rec?.Content, after, v);
    }

    public string Status(string path)
    {
        string rel = RequireNotePath(path, out string full);
        using var store = Open();
        var rec = store.Get(rel);
        var fi  = new FileInfo(full);

        var sb = new StringBuilder();
        sb.AppendLine($"path: {rel}  (target: {Name})");
        if (rec == null)
        {
            sb.AppendLine("DB: 未取込");
        }
        else
        {
            sb.AppendLine($"version: {rec.Version}");
            sb.AppendLine($"DB:   mtime {Fmt.DtTicks(rec.FileMtime)}, size {rec.FileSize}B, 本文 {rec.Content.Length}文字");
        }
        if (!fi.Exists)
            sb.AppendLine("file: 存在しません" + (rec != null ? "（外部で削除されています。read すると DB からも消えます）" : ""));
        else
            sb.AppendLine($"file: mtime {Fmt.DtTicks(fi.LastWriteTimeUtc.Ticks)}, size {fi.Length}B");

        if (rec != null && fi.Exists)
            sb.AppendLine(Importer.IsFresh(rec, fi)
                ? "一致: はい（DB とファイルは同じ状態です）"
                : "一致: いいえ（外部で変更されています。read で取り込み直すと version が上がります）");
        sb.Append($"履歴: {store.CountHistory(rel)} 件");
        return sb.ToString();
    }

    public string? Verify(string path)
    {
        string rel = Norm(path);
        string full = Resolve(rel);
        using var store = Open();
        var rec = store.Get(rel);
        if (rec == null) return File.Exists(full) ? "DB に記録がありません" : null;
        if (!File.Exists(full)) return "ファイルが存在しません";
        string disk = Importer.ReadFileNormalized(full);
        return disk == rec.Content ? null : "ファイルの内容が DB と一致しません";
    }

    // ---- wikilink -------------------------------------------------------------

    private static string ReplaceWikiLink(string content, string oldLink, string newLink)
    {
        string pattern = @"(!?\[\[)" + Regex.Escape(oldLink) + @"((?=[#|\]])[^\]\n]*)?\]\]";
        return Regex.Replace(content, pattern,
            m => m.Groups[1].Value + newLink + (m.Groups[2].Success ? m.Groups[2].Value : "") + "]]");
    }

    private int UpdateWikiLinksForNote(VaultStore store, string oldRelPath, string newRelPath)
    {
        string oldStem      = Path.GetFileNameWithoutExtension(oldRelPath);
        string newStem      = Path.GetFileNameWithoutExtension(newRelPath);
        string oldPathNoExt = oldRelPath[..^3];
        string newPathNoExt = newRelPath[..^3];
        if (oldPathNoExt.Equals(newPathNoExt, PathRules.Comparison)) return 0;

        return RewriteNotes(store, newRelPath, content =>
        {
            string updated = ReplaceWikiLink(content, oldPathNoExt, newPathNoExt);
            if (oldStem != newStem) updated = ReplaceWikiLink(updated, oldStem, newStem);
            return updated;
        });
    }

    private int UpdateWikiLinksForFolder(VaultStore store, string oldFolder, string newFolder)
    {
        string oldPrefix = oldFolder + "/";
        string newPrefix = newFolder + "/";
        string pattern   = @"(!?\[\[)" + Regex.Escape(oldPrefix) + @"([^\]\n]*\]\])";
        return RewriteNotes(store, null, content =>
            Regex.Replace(content, pattern, m => m.Groups[1].Value + newPrefix + m.Groups[2].Value));
    }

    private int RewriteNotes(VaultStore store, string? skipPath, Func<string, string> transform)
    {
        int updated = 0;
        foreach (var rel in store.ListPaths(_scope))
        {
            if (skipPath != null && rel.Equals(skipPath, PathRules.Comparison)) continue;
            try
            {
                string full = Resolve(rel);
                var rec = Fresh(store, rel, full);
                if (rec == null) continue;
                string next = transform(rec.Content);
                if (next == rec.Content) continue;
                WriteBack(store, rel, full, next, "link", rec);
                updated++;
            }
            catch { /* 個別ファイルのエラーは無視して継続 */ }
        }
        return updated;
    }
}
