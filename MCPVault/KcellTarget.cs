using System.Text;

/// <summary>
/// target=kcell: 既存の KnowledgeCell.db（KV ストア）を Vault と同じツールで扱うためのラッパー。
/// path は「セル名/キー」。version はキーごとの整数（KnowledgeCell.db の version 列、書き込みごとに +1）。scope・削除権限の対象外で常に read_write。
/// </summary>
public sealed class KcellTarget : INoteTarget
{
    private readonly string    _dbPath;
    private readonly AppConfig _cfg;

    public string Name     => "kcell";
    public bool   ReadOnly => false;

    public KcellTarget(string dbPath, AppConfig cfg)
    {
        _dbPath = dbPath;
        _cfg    = cfg;
    }

    private KnowledgeCell Open() => new(_dbPath);

    private static (string Cell, string? Key) Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ToolException("path が指定されていません（kcell では「セル名/キー」の形で指定します）。");
        string p = path.Replace('\\', '/').Trim('/');
        if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) p = p[..^3];
        int idx = p.IndexOf('/');
        if (idx < 0) return (p, null);
        string cell = p[..idx], key = p[(idx + 1)..];
        if (cell.Length == 0 || key.Length == 0) throw new ToolException($"path の形式が不正です（セル名/キー）: {path}");
        return (cell, key);
    }

    private static (string Cell, string Key) SplitKey(string? path)
    {
        var (cell, key) = Split(path);
        if (key is null) throw new ToolException($"キーが指定されていません。kcell では「セル名/キー」の形で指定します: {path}");
        return (cell, key);
    }

    private static string Join(string cell, string key) => $"{cell}/{key}";

    private static (string Cell, string Key) NewMemoKey() => ("memo", $"memo{DateTime.Now:yyyyMMddHHmmss}");

    // ---- 読み取り -------------------------------------------------------------

    public NoteView Read(string path)
    {
        var (cell, key) = SplitKey(path);
        using var kc = Open();
        var e = kc.Get(cell, key) ?? throw new ToolException($"[{cell}] キー「{key}」は存在しません。");
        return new NoteView(Join(cell, key), e.Value, e.Version, e.UpdatedAt, Encoding.UTF8.GetByteCount(e.Value));
    }

    public string List(string? folder)
    {
        using var kc = Open();
        if (string.IsNullOrWhiteSpace(folder))
        {
            var cells = kc.List().ToList();
            if (cells.Count == 0) return "KnowledgeCell にセルはありません。";
            var sb = new StringBuilder();
            sb.AppendLine($"KnowledgeCell {cells.Count}セル:");
            foreach (var (cn, cnt, lu) in cells)
                sb.AppendLine($"{cn}/  ({cnt}件, 最終更新: {Fmt.Dt(lu)})");
            return sb.ToString().TrimEnd();
        }
        var (cell, _) = Split(folder);
        var entries = kc.Read(cell).ToList();
        if (entries.Count == 0) return $"[{cell}] にエントリはありません。";
        var sb2 = new StringBuilder();
        sb2.AppendLine($"{cell}/ のキー {entries.Count}件:");
        foreach (var (k, v, ua) in entries)
            sb2.AppendLine($"  {k}  ({Fmt.Dt(ua)}, {Fmt.Size(Encoding.UTF8.GetByteCount(v))})");
        return sb2.ToString().TrimEnd();
    }

    public string Recent(int count)
    {
        using var kc = Open();
        var rows = kc.ReadAll(Math.Clamp(count, 1, 100)).ToList();
        if (rows.Count == 0) return "KnowledgeCell にエントリはありません。";
        var sb = new StringBuilder();
        sb.AppendLine($"最近更新された {rows.Count}件のエントリ:");
        int i = 1;
        foreach (var (cell, key, _, ua) in rows)
            sb.AppendLine($"{i++}. {Join(cell, key)}  ({Fmt.Dt(ua)})");
        return sb.ToString().TrimEnd();
    }

    public string Search(string query, int limit, Dictionary<string, string>? frontmatter)
    {
        var terms = VaultStore.SplitQuery(query ?? "");
        if (terms.Length == 0) throw new ToolException("query が空です。");
        using var kc = Open();
        var hits = kc.ReadAll()
            .Select(e => (e.Cell, e.Key, e.Value, e.UpdatedAt, Norm: VaultStore.Normalize(e.Value)))
            .Where(e => terms.All(t => e.Norm.Contains(t, StringComparison.Ordinal)))
            .Select(e => (e.Cell, e.Key, e.Value, e.UpdatedAt, Count: terms.Sum(t => Fmt.CountOccurrences(e.Norm, t))))
            .OrderByDescending(e => e.Count).ThenByDescending(e => e.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
        if (hits.Count == 0) return $"「{query}」に一致するエントリは見つかりませんでした。";
        var sb = new StringBuilder();
        sb.AppendLine($"「{query}」の検索結果 {hits.Count}件:");
        int i = 1;
        foreach (var h in hits)
            sb.AppendLine($"{i++}. {Join(h.Cell, h.Key)}\n   {VaultStore.BuildSnippet(h.Value, terms)}");
        return sb.ToString().TrimEnd();
    }

    public string Inbox(string to, int limit) =>
        throw new ToolException("inbox は kcell では使えません。target=main または guest を指定してください。");

    // ---- 書き込み -------------------------------------------------------------

    public WriteOutcome Write(string? path, string content, bool overwrite, long? ifVersion)
    {
        content = Importer.NormalizeText(content);
        var (cell, key) = string.IsNullOrWhiteSpace(path) ? NewMemoKey() : SplitKey(path);
        using var kc = Open();
        var cur = kc.Get(cell, key);
        if (cur != null)
        {
            if (!overwrite || ifVersion is null)
                throw new ToolException(
                    $"キーがすでに存在します: {Join(cell, key)}（version {cur.Version}）。\n" +
                    $"全文を置き換えるには overwrite=true と if_version={cur.Version} の両方を指定してください。一部の修正は edit、末尾への追加は append を使ってください。");
            if (ifVersion != cur.Version)
                throw new ToolException($"version が一致しません: {Join(cell, key)} は現在 version {cur.Version} です（指定: {ifVersion}）。");
        }
        kc.Write(cell, key, content, "write");
        long v = kc.Get(cell, key)!.Version;
        return new WriteOutcome(Join(cell, key), cur == null ? "作成しました" : "上書きしました", cur?.Value, content, v);
    }

    public WriteOutcome Edit(string path, List<(string Old, string New)> edits, bool replaceAll, long? ifVersion)
    {
        var (cell, key) = SplitKey(path);
        using var kc = Open();
        var cur = kc.Get(cell, key) ?? throw new ToolException($"[{cell}] キー「{key}」は存在しません。");
        if (ifVersion is { } iv && iv != cur.Version)
            throw new ToolException($"version が一致しません: {Join(cell, key)} は現在 version {cur.Version} です（指定: {iv}）。\n\n--- 現在の内容 ---\n{cur.Value}");

        var norm = edits.Select(e => (Importer.NormalizeText(e.Old), Importer.NormalizeText(e.New))).ToList();
        string? err = Fmt.ApplyEdits(cur.Value, norm, replaceAll, _cfg.EditMaxPairs, out string written, out int count);
        if (err != null) throw new ToolException($"{err}\n\n--- 現在の内容 ({Join(cell, key)}, version {cur.Version}) ---\n{cur.Value}");
        if (written == cur.Value) throw new ToolException($"変更がありません: {Join(cell, key)}");

        kc.Write(cell, key, written, "edit");
        long v = kc.Get(cell, key)!.Version;
        return new WriteOutcome(Join(cell, key), $"置換しました ({count}箇所)", cur.Value, written, v);
    }

    public WriteOutcome Append(string? path, string content, bool journal)
    {
        content = Importer.NormalizeText(content);
        var (cell, key) = string.IsNullOrWhiteSpace(path) ? NewMemoKey() : SplitKey(path);
        using var kc = Open();
        var cur = kc.Get(cell, key);
        string src     = cur?.Value ?? "";
        string written = src + Fmt.AppendEntry(src, content, journal);
        kc.Write(cell, key, written, "append");
        long v = kc.Get(cell, key)!.Version;
        string msg = cur == null ? "作成して追記しました" : journal ? "ジャーナル追記しました" : "追記しました";
        return new WriteOutcome(Join(cell, key), msg, cur?.Value, written, v);
    }

    // ---- 管理 -------------------------------------------------------------

    public string Move(string source, string destination)
    {
        var (sc, sk) = SplitKey(source);
        var (dc, dk) = SplitKey(destination);
        using var kc = Open();
        if (kc.Get(sc, sk) is null) throw new ToolException($"移動元が見つかりません: {Join(sc, sk)}");
        if (kc.Get(dc, dk) is not null) throw new ToolException($"移動先がすでに存在します: {Join(dc, dk)}");
        kc.Move(sc, sk, dc, dk);
        return $"移動しました: {Join(sc, sk)} → {Join(dc, dk)}";
    }

    public string Delete(string path)
    {
        var (cell, key) = Split(path);
        using var kc = Open();
        int deleted = kc.Delete(cell, key);
        if (deleted == 0)
            throw new ToolException(key != null ? $"[{cell}] キー「{key}」は存在しません。" : $"[{cell}] は存在しません。");
        return key != null
            ? $"削除しました: {Join(cell, key)}（rollback で復元できます）"
            : $"削除しました: {cell}/（{deleted}件。rollback で復元できます）";
    }

    public string Folder(string op, string path, string? destination) => op?.ToLowerInvariant() switch
    {
        "create" => throw new ToolException("kcell ではセルは最初のキーを書き込んだときに自動作成されます。folder op=create は不要です。"),
        "rename" => throw new ToolException("kcell ではセルの改名はできません。キー単位で move してください。"),
        "delete" => throw new ToolException("kcell のセルは空になりません。セル全体を消すには delete で path にセル名だけを指定してください。"),
        _        => throw new ToolException($"op は create / rename / delete のいずれかです: {op}"),
    };

    // ---- 復元・確認 -------------------------------------------------------------

    public WriteOutcome Rollback(string path, bool force)
    {
        var (cell, key) = Split(path);
        using var kc = Open();
        var before = key != null ? kc.Get(cell, key)?.Value : null;
        var r = kc.Rollback(cell) ?? throw new ToolException($"ロールバックできる履歴がありません: {cell}/");
        string target = r.Keys.Count == 1 ? Join(cell, r.Keys[0]) : $"{cell}/";
        var afterEntry = r.Keys.Count == 1 ? kc.Get(cell, r.Keys[0]) : null;
        return new WriteOutcome(target,
            $"ロールバックしました（{r.Op} を取り消し: {string.Join(", ", r.Keys)}、残り履歴 {r.Remaining} 件）",
            before, afterEntry?.Value, afterEntry?.Version ?? 0);
    }

    public string Status(string path)
    {
        var (cell, key) = SplitKey(path);
        using var kc = Open();
        var e = kc.Get(cell, key);
        if (e is null) return $"path: {Join(cell, key)}  (target: kcell)\n存在しません";
        return $"path: {Join(cell, key)}  (target: kcell)\nversion: {e.Version}（更新日時 {Fmt.Dt(e.UpdatedAt)}）\nsize: {Encoding.UTF8.GetByteCount(e.Value)}B, {e.Value.Length}文字";
    }

    public string? Verify(string path)
    {
        var (cell, key) = SplitKey(path);
        using var kc = Open();
        return kc.Get(cell, key) is null ? "キーが存在しません" : null;
    }
}
