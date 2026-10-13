using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed record ToolResult(string Text, bool IsError = false);

public static class Sync
{
    public static readonly object ToolLock = new();   // ツール呼び出しは MCP / HTTP を通じて直列化する
    public static readonly object LogLock  = new();
}

/// <summary>サーバーの状態。ツール実装から参照する。</summary>
public sealed class ServerContext
{
    public required string         AppVersion;
    public required string         BuildDate;
    public          string?        VaultPath;
    public required string         DbPath;
    public required string         KcDbPath;
    public required string         GuestDbPath;
    public          string?        LogPath;
    public required ConfigProvider Config;
    public required Action<string> Log;
    public          string[]       EnvExcluded = [];

    public string?                  GuestVaultPath;
    public CancellationTokenSource? GuestCts;

    /// <summary>(vaultPath, dbPath, ct) → ウォッチャーとフルスキャンを起動する。Program.cs が設定する。</summary>
    public Action<string, string, CancellationToken>? StartVault;

    public bool HttpRunning;
    public int  HttpPort;

    public string[] ExtraExcluded => [.. EnvExcluded, .. Config.Current.AdditionalFolders];
}

/// <summary>
/// ツール定義（tools/list）とディスパッチ（tools/call）。MCP と HTTP の両方から同じ入口を使う。
/// </summary>
public static class Tools
{
    public static readonly JsonSerializerOptions JsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // -------------------------------------------------------------------------
    // 定義
    // -------------------------------------------------------------------------

    private const string TargetProp = """
        "target": {
            "type": "string",
            "enum": ["main", "guest", "kcell"],
            "default": "main",
            "description": "Vault to operate on: main (default), guest (the vault opened with guest_open), or kcell (KnowledgeCell key-value store; path is written as 'cell/key')"
        }
        """;

    private const string ReturnProp = """
        "return": {
            "type": "string",
            "enum": ["path", "diff", "full"],
            "description": "What to return: path (path and version only), diff (plus a unified diff of the change), full (plus the note re-read from storage and a verify result)"
        }
        """;

    private static readonly string[] ToolJson =
    [
        $$"""
        {
            "name": "read",
            "description": "Reads one note (path) or several notes (paths) and returns their content with version. Content is normalized to LF. If the file changed outside MCPVault it is re-imported first, so the returned version is current.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "path":  { "type": "string", "description": "Relative path of the note (e.g. 'Daily/2026-01-15.md')" },
                    "paths": { "type": "array", "items": { "type": "string" }, "description": "Several relative paths; each note is returned under a '=== path (version N) ===' header" }
                }
            }
        }
        """,
        $$"""
        {
            "name": "list",
            "description": "Lists folders and notes as an indented tree with last-modified time and version. Optionally limited to one folder (recursive).",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "folder": { "type": "string", "description": "Folder to list (relative path). Omit for the whole vault." }
                }
            }
        }
        """,
        $$"""
        {
            "name": "recent",
            "description": "Returns the most recently modified notes, newest first.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "count": { "type": "number", "default": 10, "description": "Number of notes to return (1-100)" }
                }
            }
        }
        """,
        $$"""
        {
            "name": "search",
            "description": "Full-text search (Japanese and English). Space-separated words are ANDed. Words of 3+ characters use the FTS5 trigram index; shorter words (e.g. 2-character Japanese compounds) fall back to a full scan. Results include a snippet from the original text. frontmatter conditions (key = value) can be added, or used alone without query.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "query": { "type": "string", "description": "Search words separated by spaces (AND). May be omitted when frontmatter is given." },
                    "limit": { "type": "number", "default": 20, "description": "Maximum number of results (1-100)" },
                    "frontmatter": {
                        "type": "object",
                        "additionalProperties": { "type": "string" },
                        "description": "Frontmatter conditions, e.g. {\"status\": \"open\", \"to\": \"claude\"}. All must match."
                    }
                }
            }
        }
        """,
        $$"""
        {
            "name": "inbox",
            "description": "Lists unprocessed messages addressed to you: notes whose frontmatter 'to' equals your nickname (or notes with 'from' but no 'to', which are for everyone) and whose 'status' is not done/archived/closed. Notes under archive/ are excluded. Mark a message processed by setting status or moving it to archive/.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "to":    { "type": "string", "description": "Your nickname as written in the message frontmatter 'to'" },
                    "limit": { "type": "number", "default": 20, "description": "Maximum number of messages (1-100)" }
                },
                "required": ["to"]
            }
        }
        """,
        $$"""
        {
            "name": "write",
            "description": "Creates a note with the full content, or replaces an existing note entirely. Without path, creates memo/memoYYYYMMDDHHmmss.md. Replacing an existing note requires BOTH overwrite=true and if_version equal to the note's current version (from read or note_status). Use edit to change part of a note and append to add to the end. Files are stored as UTF-8 CRLF without BOM.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "path":       { "type": "string", "description": "Relative path (must end with .md). Omit to create a new memo under memo/." },
                    "content":    { "type": "string", "description": "Full content of the note" },
                    "overwrite":  { "type": "boolean", "default": false, "description": "Allow replacing an existing note (also requires if_version)" },
                    "if_version": { "type": "integer", "description": "Expected current version of the note; the write fails if it differs" },
                    {{ReturnProp}}
                },
                "required": ["content"]
            }
        }
        """,
        $$"""
        {
            "name": "edit",
            "description": "Replaces text inside an existing note. Pass one or more {old_str, new_str} pairs in edits; each old_str must match exactly once (include surrounding context to make it unique) unless replace_all=true. If any pair fails or if_version does not match, nothing is written and the current full content with its version is returned in the error. Default return is diff.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "path": { "type": "string", "description": "Relative path of the note (must end with .md)" },
                    "edits": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "old_str": { "type": "string", "description": "Exact text to find" },
                                "new_str": { "type": "string", "description": "Replacement text (empty string deletes old_str)" }
                            },
                            "required": ["old_str", "new_str"]
                        },
                        "description": "Replacement pairs applied in order"
                    },
                    "replace_all": { "type": "boolean", "default": false, "description": "Replace every occurrence of each old_str instead of requiring a unique match" },
                    "if_version":  { "type": "integer", "description": "Expected current version; the edit fails if it differs" },
                    {{ReturnProp}}
                },
                "required": ["path", "edits"]
            }
        }
        """,
        $$"""
        {
            "name": "append",
            "description": "Appends text to the end of a note without reading it first. Creates the note if it does not exist (without path, creates memo/memoYYYYMMDDHHmmss.md). A newline is inserted first if the note does not end with one. With journal=true the entry is prefixed with '[yyyy-MM-dd HH:mm:ss] ' (local time) and ends with a newline.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "path":    { "type": "string", "description": "Relative path (must end with .md). Omit to create a new memo under memo/." },
                    "content": { "type": "string", "description": "Text to append" },
                    "journal": { "type": "boolean", "default": false, "description": "Prefix with a local timestamp and end with a newline" },
                    {{ReturnProp}}
                },
                "required": ["content"]
            }
        }
        """,
        $$"""
        {
            "name": "move",
            "description": "Moves or renames a note and updates [[wikilinks]] in other notes that reference it. Use this instead of delete for notes that are no longer needed (e.g. move to archive/).",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "source":      { "type": "string", "description": "Current relative path" },
                    "destination": { "type": "string", "description": "New relative path (must end with .md)" }
                },
                "required": ["source", "destination"]
            }
        }
        """,
        $$"""
        {
            "name": "delete",
            "description": "Deletes a note. Disabled by default (permissions.allow_delete in mcp_config.json, set by a human); when disabled, move the note to archive/ instead. A deleted .md note can be restored with rollback.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "path": { "type": "string", "description": "Relative path of the note to delete" }
                },
                "required": ["path"]
            }
        }
        """,
        $$"""
        {
            "name": "folder",
            "description": "Folder operations: op=create makes a folder, op=rename moves/renames a folder (updates note paths and wikilinks), op=delete removes an empty folder (requires permissions.allow_folder_delete).",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "op":          { "type": "string", "enum": ["create", "rename", "delete"], "description": "Operation" },
                    "path":        { "type": "string", "description": "Relative path of the folder" },
                    "destination": { "type": "string", "description": "New relative path (rename only)" }
                },
                "required": ["op", "path"]
            }
        }
        """,
        $$"""
        {
            "name": "rollback",
            "description": "Undoes the most recent write to a note made through MCPVault (write / edit / append / delete / link update). Each call goes back one more step (up to 20 per note). Undoing the write that created a note deletes it; undoing delete restores it. Fails if the note was changed afterwards (e.g. in Obsidian) unless force=true.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "path":  { "type": "string", "description": "Relative path of the note" },
                    "force": { "type": "boolean", "default": false, "description": "Roll back even if the note changed after the last write (those changes are lost)" },
                    {{ReturnProp}}
                },
                "required": ["path"]
            }
        }
        """,
        $$"""
        {
            "name": "note_status",
            "description": "Returns a note's version, size, modification times and whether the file on disk matches the database, without returning its content. Use before write with if_version, or to check for external edits.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    {{TargetProp}},
                    "path": { "type": "string", "description": "Relative path of the note" }
                },
                "required": ["path"]
            }
        }
        """,
        """
        {
            "name": "server_info",
            "description": "Returns MCPVault server information: version, each target's connection state (main / guest / kcell), guest mode (read_only or read_write), scope, delete permission, HTTP interface state and log file path.",
            "inputSchema": { "type": "object", "properties": {} }
        }
        """,
        """
        {
            "name": "guest_open",
            "description": "Opens a second (guest) vault for temporary use and indexes it in the background. guest_* operations are selected with target=guest. The guest vault is read_only unless mcp_config.json guest.mode is read_write. Only one guest vault can be open; calling again replaces it.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "vault_path": { "type": "string", "description": "Absolute path of the guest vault root folder" }
                },
                "required": ["vault_path"]
            }
        }
        """,
        """
        {
            "name": "guest_close",
            "description": "Closes the guest vault, stops its file watcher and discards its temporary index (and its rollback history).",
            "inputSchema": { "type": "object", "properties": {} }
        }
        """,
    ];

    public static JsonArray Definitions()
    {
        var arr = new JsonArray();
        foreach (var j in ToolJson) arr.Add(JsonNode.Parse(j));
        return arr;
    }

    public static string[] Names() =>
        ToolJson.Select(j => JsonNode.Parse(j)?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToArray();

    // -------------------------------------------------------------------------
    // ディスパッチ
    // -------------------------------------------------------------------------

    public static ToolResult Call(ServerContext ctx, string? name, JsonNode? args)
    {
        // 応答テキストの改行は LF に揃える（StringBuilder.AppendLine は OS 依存のため）
        try
        {
            return new ToolResult(CallInner(ctx, name ?? "", args).Replace("\r\n", "\n"));
        }
        catch (ToolException ex)
        {
            return new ToolResult(ex.Message.Replace("\r\n", "\n"), true);
        }
        catch (Exception ex)
        {
            return new ToolResult($"エラー: {ex.Message}", true);
        }
    }

    private static string CallInner(ServerContext ctx, string name, JsonNode? a)
    {
        switch (name)
        {
            case "read":
            {
                var t = Target(ctx, a);
                var paths = StrArray(a, "paths");
                string? single = Str(a, "path");
                if (paths.Length == 0 && single != null) paths = [single];
                if (paths.Length == 0) throw new ToolException("path または paths を指定してください。");
                if (paths.Length == 1)
                {
                    var v = t.Read(paths[0]);
                    return $"=== {v.Path} (version {v.Version}) ===\n{v.Content}";
                }
                var sb = new StringBuilder();
                foreach (var p in paths)
                {
                    try
                    {
                        var v = t.Read(p);
                        sb.AppendLine($"=== {v.Path} (version {v.Version}) ===");
                        sb.AppendLine(v.Content);
                    }
                    catch (ToolException ex)
                    {
                        sb.AppendLine($"=== {p} ===");
                        sb.AppendLine($"({ex.Message})");
                    }
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            }

            case "list":
                return Target(ctx, a).List(Str(a, "folder"));

            case "recent":
                return Target(ctx, a).Recent(Int(a, "count") ?? 10);

            case "search":
                return Target(ctx, a).Search(Str(a, "query") ?? "", Int(a, "limit") ?? 20, Dict(a, "frontmatter"));

            case "inbox":
                return Target(ctx, a).Inbox(Str(a, "to") ?? "", Int(a, "limit") ?? 20);

            case "write":
            {
                var t = Target(ctx, a);
                string content = Str(a, "content") ?? throw new ToolException("content が指定されていません。");
                var o = t.Write(Str(a, "path"), content, Bool(a, "overwrite") ?? false, Long(a, "if_version"));
                return FormatWrite(t, o, Str(a, "return"), "path");
            }

            case "edit":
            {
                var t = Target(ctx, a);
                string path = Str(a, "path") ?? throw new ToolException("path が指定されていません。");
                var o = t.Edit(path, Edits(a), Bool(a, "replace_all") ?? false, Long(a, "if_version"));
                return FormatWrite(t, o, Str(a, "return"), "diff");
            }

            case "append":
            {
                var t = Target(ctx, a);
                string content = Str(a, "content") ?? throw new ToolException("content が指定されていません。");
                var o = t.Append(Str(a, "path"), content, Bool(a, "journal") ?? false);
                return FormatWrite(t, o, Str(a, "return"), "path");
            }

            case "move":
                return Target(ctx, a).Move(Str(a, "source") ?? "", Str(a, "destination") ?? "");

            case "delete":
                return Target(ctx, a).Delete(Str(a, "path") ?? throw new ToolException("path が指定されていません。"));

            case "folder":
                return Target(ctx, a).Folder(Str(a, "op") ?? "", Str(a, "path") ?? "", Str(a, "destination"));

            case "rollback":
            {
                var t = Target(ctx, a);
                string path = Str(a, "path") ?? throw new ToolException("path が指定されていません。");
                var o = t.Rollback(path, Bool(a, "force") ?? false);
                return FormatWrite(t, o, Str(a, "return"), "path");
            }

            case "note_status":
                return Target(ctx, a).Status(Str(a, "path") ?? throw new ToolException("path が指定されていません。"));

            case "server_info":
                return ServerInfo(ctx);

            case "guest_open":
                return GuestOpen(ctx, Str(a, "vault_path"));

            case "guest_close":
                return GuestClose(ctx);

            default:
                throw new ToolException($"不明なツール: {name}");
        }
    }

    // -------------------------------------------------------------------------
    // target 解決
    // -------------------------------------------------------------------------

    private static INoteTarget Target(ServerContext ctx, JsonNode? a)
    {
        var cfg = ctx.Config.Current;
        string target = (Str(a, "target") ?? "main").ToLowerInvariant();
        switch (target)
        {
            case "main":
                if (ctx.VaultPath == null || !Directory.Exists(ctx.VaultPath))
                    throw new ToolException($"メイン Vault が見つかりません: {ctx.VaultPath ?? "(未設定)"}。--vault の指定を確認してください。");
                if (!File.Exists(ctx.DbPath))
                    throw new ToolException("インデックスが準備できていません。しばらく待ってから再試行してください。");
                return new VaultTarget("main", ctx.VaultPath, ctx.DbPath, readOnly: false, cfg.ScopeMain, cfg);

            case "guest":
                if (ctx.GuestVaultPath == null)
                    throw new ToolException("ゲスト Vault が開かれていません。guest_open で開いてください（開けるのは AI 自身です）。");
                if (!File.Exists(ctx.GuestDbPath))
                    throw new ToolException("ゲスト Vault のインデックスが準備中です。しばらく待ってから再試行してください。");
                return new VaultTarget("guest", ctx.GuestVaultPath, ctx.GuestDbPath, readOnly: cfg.GuestReadOnly, cfg.ScopeGuest, cfg);

            case "kcell":
                return new KcellTarget(ctx.KcDbPath, cfg);

            default:
                throw new ToolException($"target は main / guest / kcell のいずれかです: {target}");
        }
    }

    // -------------------------------------------------------------------------
    // 書き込み結果の整形（return 引数）
    // -------------------------------------------------------------------------

    private static string FormatWrite(INoteTarget t, WriteOutcome o, string? ret, string defaultRet)
    {
        string mode = (ret ?? defaultRet).ToLowerInvariant();
        string size = o.After != null ? $", {Fmt.Size(Encoding.UTF8.GetByteCount(o.After))}" : "";
        var sb = new StringBuilder($"{o.Message}: {o.Path} (version {o.Version}{size})");

        switch (mode)
        {
            case "diff":
                sb.Append("\n\n").Append(TextDiff.Unified(o.Before ?? "", o.After ?? ""));
                break;

            case "full":
                if (o.After is null)
                {
                    sb.Append("\n\n（ノートは存在しなくなりました）");
                    break;
                }
                string? verify = t.Verify(o.Path);
                sb.Append("\n\nverify: ").Append(verify ?? "OK（保存先と DB は一致しています）");
                var view = t.Read(o.Path);
                sb.Append($"\n\n=== {view.Path} (version {view.Version}) ===\n{view.Content}");
                break;

            case "path":
                break;

            default:
                throw new ToolException($"return は path / diff / full のいずれかです: {ret}");
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // server_info / guest
    // -------------------------------------------------------------------------

    private static string ServerInfo(ServerContext ctx)
    {
        var cfg = ctx.Config.Current;
        var sb  = new StringBuilder();
        sb.AppendLine($"MCPVault {ctx.AppVersion} (build {ctx.BuildDate})");

        sb.AppendLine($"main: {ctx.VaultPath ?? "(未設定)"}");
        sb.AppendLine($"  ノート: {CountNotes(ctx.DbPath, cfg.ScopeMain)}");
        sb.AppendLine($"  scope: {(cfg.ScopeMain.Length == 0 ? "(全体)" : string.Join(", ", cfg.ScopeMain))}");
        sb.AppendLine($"  削除権限: ノート {(cfg.AllowDelete ? "ON" : "OFF")} / フォルダ {(cfg.AllowFolderDelete ? "ON" : "OFF")}");

        if (ctx.GuestVaultPath != null)
        {
            sb.AppendLine($"guest: {ctx.GuestVaultPath}  ({(cfg.GuestReadOnly ? "read_only" : "read_write")})");
            sb.AppendLine($"  ノート: {CountNotes(ctx.GuestDbPath, cfg.ScopeGuest)}");
            sb.AppendLine($"  scope: {(cfg.ScopeGuest.Length == 0 ? "(全体)" : string.Join(", ", cfg.ScopeGuest))}");
        }
        else
            sb.AppendLine($"guest: (未接続。guest_open で開く。モードは {(cfg.GuestReadOnly ? "read_only" : "read_write")})");

        sb.AppendLine($"kcell: {ctx.KcDbPath}");
        try { using var kc = new KnowledgeCell(ctx.KcDbPath); sb.AppendLine($"  セル: {kc.List().Count()} 件"); }
        catch (Exception ex) { sb.AppendLine($"  取得エラー: {ex.Message}"); }

        sb.AppendLine(ctx.HttpRunning ? $"http: 127.0.0.1:{ctx.HttpPort}" : $"http: 無効{(cfg.HttpEnabled ? "（起動に失敗。ログ参照）" : "")}");
        sb.AppendLine($"ログ: {ctx.LogPath ?? "(無効)"}");
        sb.Append($"設定: {ctx.Config.Path}（人間が編集。AI からは変更できません）");
        return sb.ToString();
    }

    private static string CountNotes(string dbPath, string[] scope)
    {
        if (!File.Exists(dbPath)) return "インデックス未作成";
        try { using var store = new VaultStore(dbPath); return $"{store.CountNotes(scope)} 件"; }
        catch (Exception ex) { return $"取得エラー: {ex.Message}"; }
    }

    private static string GuestOpen(ServerContext ctx, string? vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath)) throw new ToolException("vault_path が指定されていません。");
        if (!Directory.Exists(vaultPath))         throw new ToolException($"フォルダが見つかりません: {vaultPath}");
        if (ctx.StartVault == null)               throw new ToolException("ゲスト Vault を起動できません（内部エラー）。");

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultPath));
        if (ctx.VaultPath != null && full.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ctx.VaultPath)), PathRules.Comparison))
            throw new ToolException("メイン Vault と同じフォルダはゲストとして開けません。");

        CloseGuest(ctx);
        ctx.GuestVaultPath = full;
        ctx.GuestCts       = new CancellationTokenSource();
        ctx.StartVault(full, ctx.GuestDbPath, ctx.GuestCts.Token);

        var cfg = ctx.Config.Current;
        return $"ゲスト Vault を開きました: {full}（{(cfg.GuestReadOnly ? "read_only" : "read_write")}、インデックス構築中）。target=guest で操作してください。";
    }

    private static string GuestClose(ServerContext ctx)
    {
        if (ctx.GuestVaultPath == null) throw new ToolException("ゲスト Vault は開かれていません。");
        string closed = ctx.GuestVaultPath;
        CloseGuest(ctx);
        return $"ゲスト Vault を閉じました: {closed}";
    }

    private static void CloseGuest(ServerContext ctx)
    {
        ctx.GuestCts?.Cancel();
        ctx.GuestCts?.Dispose();
        ctx.GuestCts       = null;
        ctx.GuestVaultPath = null;
        try { VaultStore.DeleteDatabase(ctx.GuestDbPath); } catch (Exception ex) { ctx.Log($"[GUEST] DB delete failed: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // 引数の取り出し
    // -------------------------------------------------------------------------

    private static string? Str(JsonNode? a, string key)
    {
        var n = a?[key];
        if (n is null) return null;
        try { return n.GetValue<string>(); }
        catch { return n.ToJsonString(); }
    }

    private static bool? Bool(JsonNode? a, string key)
    {
        var n = a?[key];
        if (n is null) return null;
        try { return n.GetValue<bool>(); }
        catch { return bool.TryParse(n.ToString(), out var b) ? b : null; }
    }

    private static int? Int(JsonNode? a, string key)
    {
        var n = a?[key];
        if (n is null) return null;
        try { return (int)n.GetValue<double>(); }
        catch { return int.TryParse(n.ToString(), out var i) ? i : null; }
    }

    private static long? Long(JsonNode? a, string key)
    {
        var n = a?[key];
        if (n is null) return null;
        try { return (long)n.GetValue<double>(); }
        catch { return long.TryParse(n.ToString(), out var l) ? l : null; }
    }

    private static string[] StrArray(JsonNode? a, string key)
    {
        var n = a?[key];
        if (n is not JsonArray arr) return [];
        return arr.Select(x => x?.GetValue<string>()).OfType<string>().ToArray();
    }

    private static Dictionary<string, string>? Dict(JsonNode? a, string key)
    {
        if (a?[key] is not JsonObject obj) return null;
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in obj)
            if (v != null) d[k] = v is JsonValue ? (Str(obj, k) ?? "") : v.ToJsonString();
        return d.Count == 0 ? null : d;
    }

    private static List<(string Old, string New)> Edits(JsonNode? a)
    {
        var list = new List<(string, string)>();
        if (a?["edits"] is JsonArray arr)
        {
            foreach (var e in arr)
            {
                if (e is null) continue;
                string? o = Str(e, "old_str") ?? Str(e, "old_text");
                string? n = Str(e, "new_str") ?? Str(e, "new_text");
                if (o is null || n is null) throw new ToolException("edits の各要素には old_str と new_str が必要です。");
                list.Add((o, n));
            }
        }
        else if (Str(a, "old_str") is { } o)
        {
            // 単発置換の簡易指定
            list.Add((o, Str(a, "new_str") ?? throw new ToolException("new_str が指定されていません。")));
        }
        if (list.Count == 0) throw new ToolException("edits が指定されていません。old_str / new_str のペアを配列で渡してください。");
        return list;
    }
}
