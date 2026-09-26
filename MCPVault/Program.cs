using System.Collections.Concurrent;
using System.IO.Ports;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// MCPVault - スタンドアロン MCP サーバー
// Claude ↔ stdin/stdout ↔ [MCPVault] ↔ Obsidian Vault（ファイルシステム直接アクセス）

var _asm = Assembly.GetExecutingAssembly();
string AppVersion = _asm.GetName().Version?.ToString(3) ?? "0.0.0";
string BuildDate  = _asm.GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "unknown";

// UTF-8 強制（Windows デフォルトは UTF-8 でないため MCP の JSON が壊れる）
Console.InputEncoding  = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

// コマンドライン引数から --vault <パス> / --com <ポート名> / --log [ファイル名] を取得
// string? comPort = null;
string? logPath = null;
string? vaultArg = null;
bool logUseDefault = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--vault", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        vaultArg = args[++i];
    // else if (args[i].Equals("--com", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    //     comPort = args[++i];
    else if (args[i].Equals("--log", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            logPath = args[++i];
        else
            logUseDefault = true;
    }
}

// 設定
string? VAULT_PATH  = vaultArg ?? Environment.GetEnvironmentVariable("OBSIDIAN_VAULT_PATH") ?? @"C:\Vault";
string mcpVaultDir  = Path.Combine(VAULT_PATH, ".MCPVault");
string DB_PATH      = Path.Combine(mcpVaultDir, "notes.db");
string KC_DB_PATH   = Path.Combine(mcpVaultDir, "KnowledgeCell.db");
// セミコロン区切りで除外フォルダを追加可能 例: "Tools3;Private"
string[] EXTRA_EXCLUDED = (Environment.GetEnvironmentVariable("MCP_EXCLUDED_FOLDERS") ?? "")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// ゲストVault状態（立ち上げ後に guest_open で追加できる一時Vault）
string? guestVaultPath = null;
string GUEST_DB_PATH = Path.Combine(mcpVaultDir, "guestnotes.db");
CancellationTokenSource? guestWatcherCts = null;

// .MCPVault フォルダが存在しない場合は初回セットアップ（デフォルト設定ファイルを生成）
string configFilePath = Path.Combine(mcpVaultDir, "mcp_config.json");
if (!Directory.Exists(mcpVaultDir))
{
    try
    {
        Directory.CreateDirectory(mcpVaultDir);
        File.WriteAllText(configFilePath, BuildDefaultConfig(), Encoding.UTF8);
    }
    catch (Exception ex) { Console.Error.WriteLine($"MCPVault init error: {ex.Message}"); }
}

// Vault ルートに README.md がなければ生成する
if (VAULT_PATH != null && Directory.Exists(VAULT_PATH))
{
    string readmePath = Path.Combine(VAULT_PATH, "README.md");
    if (!File.Exists(readmePath))
    {
        try { File.WriteAllText(readmePath, BuildWelcomeReadme(), Encoding.UTF8); }
        catch (Exception ex) { Console.Error.WriteLine($"README.md init error: {ex.Message}"); }
    }
}

// 設定ファイルから追加除外フォルダおよびパターン設定を読み込む
bool excludeDotFolders        = false;
bool excludeUnderscoreFolders = false;
bool excludeIdeFolders        = false;
bool includeFolderNames       = false;

if (File.Exists(configFilePath))
{
    try
    {
        var configJson = JsonNode.Parse(File.ReadAllText(configFilePath, Encoding.UTF8));

        var excl = configJson?["exclusions"];

        var additional = excl?["additional_folders"]?.AsArray();
        if (additional != null)
        {
            var fromConfig = additional
                .Select(f => f?.GetValue<string>())
                .OfType<string>()
                .ToArray();
            EXTRA_EXCLUDED = [.. EXTRA_EXCLUDED, .. fromConfig];
        }

        excludeDotFolders        = excl?["exclude_dot_folders"]?.GetValue<bool>()        ?? false;
        excludeUnderscoreFolders = excl?["exclude_underscore_folders"]?.GetValue<bool>() ?? false;
        excludeIdeFolders        = excl?["exclude_ide_folders"]?.GetValue<bool>()        ?? false;

        includeFolderNames = configJson?["indexing"]?["include_folder_names"]?.GetValue<bool>() ?? false;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Config file error: {ex.Message}");
    }
}

// --log のパス未指定時は DB_PATH と同ディレクトリに出力
if (logUseDefault)
    logPath = Path.Combine(Path.GetDirectoryName(DB_PATH)!, ".log", $"mcp_vault_{DateTime.Now:yyyyMMdd_HHmmss}.log");

// 仮想 COM ポートオープン（--com 指定時のみ）[コメントアウト中]
SerialPort? serial = null;
/*
if (comPort != null)
{
    try
    {
        serial = new SerialPort(comPort, BAUD_RATE);
        serial.Open();
    }
    catch (Exception ex) { Console.Error.WriteLine($"COM port error: {ex.Message}"); }
}
*/

// ログファイルオープン（--log 指定時のみ）
StreamWriter? logWriter = null;
if (logPath != null)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        logWriter = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
        logWriter.WriteLine($"=== MCPVault started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }
    catch (Exception ex) { Console.Error.WriteLine($"Log file error: {ex.Message}"); }
}

// エラーログ（常時オープン・DB と同ディレクトリ）
string errorLogPath = Path.Combine(Path.GetDirectoryName(DB_PATH)!, ".log", "mcpvault_error.log");
StreamWriter? errorLogWriter = null;
try
{
    Directory.CreateDirectory(Path.GetDirectoryName(errorLogPath)!);
    errorLogWriter = new StreamWriter(errorLogPath, append: true, Encoding.UTF8) { AutoFlush = true };
    var toolNames = GetToolNames();
    errorLogWriter.WriteLine($"=== MCPVault {AppVersion} (build {BuildDate})  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    errorLogWriter.WriteLine($"    Tools({toolNames.Length}): {string.Join(", ", toolNames)}");
}
catch (Exception ex) { Console.Error.WriteLine($"Error log open failed: {ex.Message}"); }

// KnowledgeCell DB を初期化
try { using var kc = new KnowledgeCell(KC_DB_PATH); }
catch (Exception ex) { Console.Error.WriteLine($"KnowledgeCell init error: {ex.Message}"); }

// 起動時にゲストDBの残骸を削除（前回 guest_close 失敗 / クラッシュ時の残留対策）
if (File.Exists(GUEST_DB_PATH))
{
    try { File.Delete(GUEST_DB_PATH); }
    catch (Exception ex) { Console.Error.WriteLine($"Guest DB cleanup error: {ex.Message}"); }
}

// ウォッチャーを先に起動し、並行してフルスキャンを行う
if (VAULT_PATH != null && Directory.Exists(VAULT_PATH))
{
    _ = Task.Run(() => WatchVaultChanges(VAULT_PATH, DB_PATH, excludeDotFolders, excludeUnderscoreFolders, excludeIdeFolders, serial, logWriter));
    _ = Task.Run(() => BuildVaultIndex(VAULT_PATH, DB_PATH, EXTRA_EXCLUDED, excludeDotFolders, excludeUnderscoreFolders, excludeIdeFolders, includeFolderNames, serial, logWriter));
}
else if (VAULT_PATH != null)
    Log(serial, logWriter, $"[IDX] WARN: OBSIDIAN_VAULT_PATH が見つかりません: {VAULT_PATH}");

Log(serial, logWriter, "[MCPVault] ready");

// メインループ（JSON-RPC 2.0 リクエストを処理）
string? input;
while ((input = await Console.In.ReadLineAsync()) != null)
{
    Log(serial, logWriter, $"[REQ] {input}");
    string? response = null;
    string? currentToolName = null;
    try
    {
        var req    = JsonNode.Parse(input);
        string? method = req?["method"]?.GetValue<string>();
        var id = req?["id"];

        if (method == "initialize")
        {
            response = BuildInitializeResponse(id, AppVersion);
            Log(serial, logWriter, "[MCPVault] initialize");
        }
        else if (method?.StartsWith("notifications/") == true)
        {
            // 通知は応答不要
            Log(serial, logWriter, $"[MCPVault] notification: {method}");
        }
        else if (method == "tools/list")
        {
            response = BuildToolsListResponse(id);
            Log(serial, logWriter, "[MCPVault] tools/list");
        }
        else if (method == "tools/call")
        {
            string? toolName = req?["params"]?["name"]?.GetValue<string>();
            var pargs = req?["params"]?["arguments"];
            Log(serial, logWriter, $"[MCPVault] tools/call {toolName}");
            currentToolName = toolName;

            if (toolName == "guest_open")
            {
                string? guestVp = pargs?["vault_path"]?.GetValue<string>();
                string guestMsg;
                if (string.IsNullOrWhiteSpace(guestVp))
                {
                    guestMsg = "vault_path が指定されていません。";
                }
                else if (!Directory.Exists(guestVp))
                {
                    guestMsg = $"フォルダが見つかりません: {guestVp}";
                }
                else
                {
                    // 既存ゲストVaultを閉じてから開く
                    guestWatcherCts?.Cancel();
                    guestWatcherCts?.Dispose();
                    guestWatcherCts = null;
                    if (File.Exists(GUEST_DB_PATH)) try { File.Delete(GUEST_DB_PATH); } catch { }

                    guestVaultPath  = guestVp;
                    guestWatcherCts = new CancellationTokenSource();
                    var _guestCt    = guestWatcherCts.Token;
                    string _guestVp = guestVp;
                    _ = Task.Run(() => WatchVaultChanges(_guestVp, GUEST_DB_PATH, excludeDotFolders, excludeUnderscoreFolders, excludeIdeFolders, serial, logWriter, _guestCt));
                    _ = Task.Run(() => BuildVaultIndex(_guestVp, GUEST_DB_PATH, EXTRA_EXCLUDED, excludeDotFolders, excludeUnderscoreFolders, excludeIdeFolders, includeFolderNames, serial, logWriter));
                    guestMsg = $"ゲストVaultを開きました: {guestVp}（インデックス構築中）";
                }
                var _jOpt = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"]      = id?.DeepClone(),
                    ["result"]  = new JsonObject
                    {
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = guestMsg } }
                    }
                }.ToJsonString(_jOpt);
            }
            else if (toolName == "guest_close")
            {
                string guestMsg;
                if (guestVaultPath == null)
                {
                    guestMsg = "ゲストVaultが開かれていません。";
                }
                else
                {
                    string closedPath = guestVaultPath;
                    guestWatcherCts?.Cancel();
                    guestWatcherCts?.Dispose();
                    guestWatcherCts = null;
                    guestVaultPath  = null;
                    if (File.Exists(GUEST_DB_PATH)) try { File.Delete(GUEST_DB_PATH); } catch { }
                    guestMsg = $"ゲストVaultを閉じました: {closedPath}";
                }
                var _jOpt = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"]      = id?.DeepClone(),
                    ["result"]  = new JsonObject
                    {
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = guestMsg } }
                    }
                }.ToJsonString(_jOpt);
            }
            else
            {
            response = toolName switch
            {
                "list_notes" => File.Exists(DB_PATH)
                    ? BuildListNotesResponse(id, pargs?["folder"]?.GetValue<string>(), DB_PATH)
                    : BuildErrorResponse(id, "インデックスが準備できていません。しばらく待ってから再試行してください。"),

                "search_notes" => File.Exists(DB_PATH)
                    ? BuildSearchNotesResponse(id,
                        pargs?["query"]?.GetValue<string>(),
                        DB_PATH,
                        pargs?["limit"]?.GetValue<int>() ?? 20)
                    : BuildErrorResponse(id, "インデックスが準備できていません。しばらく待ってから再試行してください。"),

                "recent_notes" => File.Exists(DB_PATH)
                    ? BuildRecentNotesResponse(id,
                        pargs?["count"]?.GetValue<int>() ?? 10,
                        DB_PATH)
                    : BuildErrorResponse(id, "インデックスが準備できていません。しばらく待ってから再試行してください。"),

                "read_note" => VAULT_PATH != null
                    ? BuildReadNoteResponse(id, pargs?["path"]?.GetValue<string>(), VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "read_notes" => VAULT_PATH != null
                    ? BuildReadMultipleNotesResponse(id,
                        pargs?["paths"]?.AsArray()
                            ?.Select(p => p?.GetValue<string>()).OfType<string>().ToArray() ?? [],
                        VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "update_note" => VAULT_PATH != null
                    ? BuildUpdateNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["content"]?.GetValue<string>(),
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "append_note" => VAULT_PATH != null
                    ? BuildAppendNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["content"]?.GetValue<string>(),
                        pargs?["journal"]?.GetValue<bool>() ?? false,
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "edit_note" => VAULT_PATH != null
                    ? BuildEditNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["old_text"]?.GetValue<string>(),
                        pargs?["new_text"]?.GetValue<string>(),
                        pargs?["replace_all"]?.GetValue<bool>() ?? false,
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "rollback_note" => VAULT_PATH != null
                    ? BuildRollbackNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["force"]?.GetValue<bool>()     ?? false,
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "create_note" => VAULT_PATH != null
                    ? BuildCreateNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["content"]?.GetValue<string>(),
                        pargs?["template"]?.GetValue<string>(),
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "delete_note" => VAULT_PATH != null
                    ? BuildDeleteNoteResponse(id, pargs?["path"]?.GetValue<string>(), VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "move_note" => VAULT_PATH != null
                    ? BuildMoveNoteResponse(id,
                        pargs?["sourcePath"]?.GetValue<string>(),
                        pargs?["destinationPath"]?.GetValue<string>(),
                        VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "list_folders" => File.Exists(DB_PATH)
                    ? BuildListFoldersResponse(id, DB_PATH)
                    : BuildErrorResponse(id, "インデックスが準備できていません。しばらく待ってから再試行してください。"),

                "create_folder" => VAULT_PATH != null
                    ? BuildCreateFolderResponse(id, pargs?["path"]?.GetValue<string>(), VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "rename_folder" => VAULT_PATH != null
                    ? BuildRenameFolderResponse(id,
                        pargs?["sourcePath"]?.GetValue<string>(),
                        pargs?["destinationPath"]?.GetValue<string>(),
                        VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "delete_empty_folder" => VAULT_PATH != null
                    ? BuildDeleteEmptyFolderResponse(id, pargs?["path"]?.GetValue<string>(), VAULT_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "kcell_write" => BuildKcellWriteResponse(id,
                    pargs?["cell_name"]?.GetValue<string>(),
                    pargs?["key"]?.GetValue<string>(),
                    pargs?["value"]?.GetValue<string>(),
                    pargs?["read_back"]?.GetValue<bool>() ?? false,
                    KC_DB_PATH),

                "kcell_append" => BuildKcellAppendResponse(id,
                    pargs?["cell_name"]?.GetValue<string>(),
                    pargs?["key"]?.GetValue<string>(),
                    pargs?["value"]?.GetValue<string>(),
                    pargs?["journal"]?.GetValue<bool>()   ?? false,
                    pargs?["read_back"]?.GetValue<bool>() ?? false,
                    KC_DB_PATH),

                "kcell_edit" => BuildKcellEditResponse(id,
                    pargs?["cell_name"]?.GetValue<string>(),
                    pargs?["key"]?.GetValue<string>(),
                    pargs?["old_text"]?.GetValue<string>(),
                    pargs?["new_text"]?.GetValue<string>(),
                    pargs?["replace_all"]?.GetValue<bool>() ?? false,
                    pargs?["read_back"]?.GetValue<bool>()   ?? false,
                    KC_DB_PATH),

                "kcell_rollback" => BuildKcellRollbackResponse(id,
                    pargs?["cell_name"]?.GetValue<string>(),
                    pargs?["read_back"]?.GetValue<bool>() ?? false,
                    KC_DB_PATH),

                "kcell_read" => BuildKcellReadResponse(id,
                    pargs?["cell_name"]?.GetValue<string>(),
                    pargs?["key"]?.GetValue<string>(),
                    pargs?["latest"]?.GetValue<bool>() ?? false,
                    KC_DB_PATH),

                "kcell_delete" => BuildKcellDeleteResponse(id,
                    pargs?["cell_name"]?.GetValue<string>(),
                    pargs?["key"]?.GetValue<string>(),
                    KC_DB_PATH),

                "kcell_list" => BuildKcellListResponse(id, KC_DB_PATH),

                "server_info" => BuildServerInfoResponse(id, AppVersion, BuildDate,
                    VAULT_PATH, DB_PATH, guestVaultPath, GUEST_DB_PATH, logPath),

                // ゲストVaultツール群（guest_open / guest_close は上の if-else で処理）
                "guest_list_notes" => guestVaultPath != null && File.Exists(GUEST_DB_PATH)
                    ? BuildListNotesResponse(id, pargs?["folder"]?.GetValue<string>(), GUEST_DB_PATH)
                    : BuildErrorResponse(id, guestVaultPath == null ? "ゲストVaultが開かれていません。guest_open で開いてください。" : "インデックスが準備中です。しばらく待ってから再試行してください。"),

                "guest_recent_notes" => guestVaultPath != null && File.Exists(GUEST_DB_PATH)
                    ? BuildRecentNotesResponse(id, pargs?["count"]?.GetValue<int>() ?? 10, GUEST_DB_PATH)
                    : BuildErrorResponse(id, guestVaultPath == null ? "ゲストVaultが開かれていません。guest_open で開いてください。" : "インデックスが準備中です。しばらく待ってから再試行してください。"),

                "guest_search_notes" => guestVaultPath != null && File.Exists(GUEST_DB_PATH)
                    ? BuildSearchNotesResponse(id, pargs?["query"]?.GetValue<string>(), GUEST_DB_PATH, pargs?["limit"]?.GetValue<int>() ?? 20)
                    : BuildErrorResponse(id, guestVaultPath == null ? "ゲストVaultが開かれていません。guest_open で開いてください。" : "インデックスが準備中です。しばらく待ってから再試行してください。"),

                "guest_read_note" => guestVaultPath != null
                    ? BuildReadNoteResponse(id, pargs?["path"]?.GetValue<string>(), guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_read_notes" => guestVaultPath != null
                    ? BuildReadMultipleNotesResponse(id,
                        pargs?["paths"]?.AsArray()
                            ?.Select(p => p?.GetValue<string>()).OfType<string>().ToArray() ?? [],
                        guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_create_note" => guestVaultPath != null
                    ? BuildCreateNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["content"]?.GetValue<string>(),
                        pargs?["template"]?.GetValue<string>(),
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_update_note" => guestVaultPath != null
                    ? BuildUpdateNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["content"]?.GetValue<string>(),
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_append_note" => guestVaultPath != null
                    ? BuildAppendNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["content"]?.GetValue<string>(),
                        pargs?["journal"]?.GetValue<bool>() ?? false,
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_edit_note" => guestVaultPath != null
                    ? BuildEditNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["old_text"]?.GetValue<string>(),
                        pargs?["new_text"]?.GetValue<string>(),
                        pargs?["replace_all"]?.GetValue<bool>() ?? false,
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_rollback_note" => guestVaultPath != null
                    ? BuildRollbackNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["force"]?.GetValue<bool>()     ?? false,
                        pargs?["read_back"]?.GetValue<bool>() ?? false,
                        guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_delete_note" => guestVaultPath != null
                    ? BuildDeleteNoteResponse(id, pargs?["path"]?.GetValue<string>(), guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_move_note" => guestVaultPath != null
                    ? BuildMoveNoteResponse(id,
                        pargs?["sourcePath"]?.GetValue<string>(),
                        pargs?["destinationPath"]?.GetValue<string>(),
                        guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_list_folders" => guestVaultPath != null && File.Exists(GUEST_DB_PATH)
                    ? BuildListFoldersResponse(id, GUEST_DB_PATH)
                    : BuildErrorResponse(id, guestVaultPath == null ? "ゲストVaultが開かれていません。guest_open で開いてください。" : "インデックスが準備中です。しばらく待ってから再試行してください。"),

                "guest_create_folder" => guestVaultPath != null
                    ? BuildCreateFolderResponse(id, pargs?["path"]?.GetValue<string>(), guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_rename_folder" => guestVaultPath != null
                    ? BuildRenameFolderResponse(id,
                        pargs?["sourcePath"]?.GetValue<string>(),
                        pargs?["destinationPath"]?.GetValue<string>(),
                        guestVaultPath, GUEST_DB_PATH)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                "guest_delete_empty_folder" => guestVaultPath != null
                    ? BuildDeleteEmptyFolderResponse(id, pargs?["path"]?.GetValue<string>(), guestVaultPath)
                    : BuildErrorResponse(id, "ゲストVaultが開かれていません。guest_open で開いてください。"),

                _ => BuildErrorResponse(id, $"不明なツール: {toolName}")
            };
            } // end else (not guest_open/guest_close)
        }
        else if (method == "resources/list" && File.Exists(DB_PATH))
        {
            response = BuildResourcesListResponse(id, DB_PATH);
            Log(serial, logWriter, "[MCPVault] resources/list");
        }
        else if (id != null)
        {
            var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var err = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = id.DeepClone(),
                ["error"]   = new JsonObject
                {
                    ["code"]    = -32601,
                    ["message"] = $"Method not found: {method}"
                }
            };
            response = err.ToJsonString(jsonOptions);
        }
    }
    catch { }

    if (response != null)
    {
        await Console.Out.WriteLineAsync(response);
        await Console.Out.FlushAsync();
        Log(serial, logWriter, $"[OUT] {response}");

        // isError:true のレスポンスはエラーログにも記録
        try
        {
            var outNode = JsonNode.Parse(response);
            if (outNode?["result"]?["isError"]?.GetValue<bool>() == true)
            {
                string errText = outNode["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "(no message)";
                LogError(errorLogWriter, $"[ERR] {currentToolName ?? "?"}: {errText}");
            }
        }
        catch { }
    }
}

// serial?.Close();
logWriter?.WriteLine($"=== MCPVault stopped {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
logWriter?.Close();
errorLogWriter?.Close();

// ファイルサイズを人間が読みやすい形式に変換（文字数ベース）
static string FormatSize(int chars) => chars switch
{
    < 1_024             => $"{chars}B",
    < 1_024 * 1_024     => $"{chars / 1_024.0:F1}KB",
    _                   => $"{chars / (1_024.0 * 1_024):F1}MB",
};

// ログ出力（仮想 COM ポート / ファイルへ）— 複数スレッドから呼ばれるため lock で保護
static void Log(SerialPort? port, StreamWriter? writer, string message)
{
    lock (Sync.LogLock)
    {
        // try { port?.WriteLine(message); }
        // catch { /* COM ポートエラーは無視 */ }

        if (writer != null)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            try { writer.WriteLine(line); }
            catch { /* ログエラーは無視 */ }
        }
    }
}

// エラーログ出力
static void LogError(StreamWriter? writer, string message)
{
    if (writer == null) return;
    lock (Sync.LogLock)
    {
        try { writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}"); }
        catch { }
    }
}

// tools/list からツール名一覧を取得する（起動時ログ用）
static string[] GetToolNames()
{
    try
    {
        var node  = JsonNode.Parse(BuildToolsListResponse(null));
        var tools = node?["result"]?["tools"]?.AsArray();
        return tools?
            .Select(t => t?["name"]?.GetValue<string>() ?? "")
            .Where(n => n.Length > 0)
            .ToArray() ?? [];
    }
    catch { return []; }
}

// Vault の .md ファイルを監視してインデックスを差分更新する
static void WatchVaultChanges(string vaultPath, string dbPath, bool exDot, bool exUnderscore, bool exIde, SerialPort? serial, StreamWriter? mainLog, CancellationToken ct = default)
{
    try
    {
        // 500ms デバウンス（複数イベントをまとめる）
        var debounce = new ConcurrentDictionary<string, CancellationTokenSource>();

        void Debounce(string fullPath, Action action)
        {
            if (debounce.TryRemove(fullPath, out var old)) old.Cancel();
            var cts = new CancellationTokenSource();
            debounce[fullPath] = cts;
            Task.Delay(500, cts.Token).ContinueWith(t =>
            {
                debounce.TryRemove(fullPath, out _);
                if (!t.IsCanceled) action();
            }, TaskScheduler.Default);
        }

        void UpsertFile(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath)) return;
                if (IsUnderNoCrawl(fullPath, vaultPath)) return;

                using var index = new NoteIndex(dbPath);
                var excludedSet = index.GetExcludedFolders()
                    .Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

                string relative = Path.GetRelativePath(vaultPath, fullPath).Replace('\\', '/');
                string folder   = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";

                bool excluded = excludedSet.Any(e =>
                    folder.Equals(e, StringComparison.OrdinalIgnoreCase) ||
                    folder.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase))
                    || IsPatternExcluded(folder, exDot, exUnderscore, exIde);
                if (excluded) return;

                long now     = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var fi       = new FileInfo(fullPath);
                long lastMod = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();

                string? fileContent = null;
                string? fileHash    = null;
                try
                {
                    fileContent = File.ReadAllText(fullPath, Encoding.UTF8);
                    fileHash    = NoteIndex.ComputeHash(fileContent);
                }
                catch (Exception ex)
                {
                    Log(serial, mainLog, $"[IDX] WARN: content read failed (watch) {relative}: {ex.Message}");
                }

                var entry    = new NoteEntry(relative,
                    string.IsNullOrEmpty(folder) ? null : folder,
                    Path.GetFileName(fullPath), lastMod, now, fileContent, fileHash);

                var result = index.UpsertIfNewer(entry);
                if (result != UpsertResult.Unchanged)
                {
                    string sizeLabel = fileContent != null ? $" {FormatSize(fileContent.Length)}" : " (no content)";
                    Log(serial, mainLog, $"[IDX] {result.ToString().ToUpper()} (watch) {relative}{sizeLabel}");
                }
            }
            catch (Exception ex) { Log(serial, mainLog, $"[IDX] Watch upsert error: {ex.Message}"); }
        }

        void DeleteFile(string fullPath)
        {
            try
            {
                using var index = new NoteIndex(dbPath);
                string relative = Path.GetRelativePath(vaultPath, fullPath).Replace('\\', '/');
                index.Delete(relative);
                Log(serial, mainLog, $"[IDX] DEL (watch) {relative}");
            }
            catch (Exception ex) { Log(serial, mainLog, $"[IDX] Watch delete error: {ex.Message}"); }
        }

        var watcher = new FileSystemWatcher(vaultPath)
        {
            Filter               = "*.md",
            IncludeSubdirectories = true,
            NotifyFilter         = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            InternalBufferSize   = 65536,   // デフォルト 8KB → 64KB（バッファオーバーフロー対策）
        };

        watcher.Created  += (_, e) => Debounce(e.FullPath, () => UpsertFile(e.FullPath));
        watcher.Changed  += (_, e) => Debounce(e.FullPath, () => UpsertFile(e.FullPath));
        watcher.Deleted  += (_, e) => DeleteFile(e.FullPath);
        watcher.Renamed  += (_, e) => { DeleteFile(e.OldFullPath); Debounce(e.FullPath, () => UpsertFile(e.FullPath)); };
        watcher.Error    += (_, e) => Log(serial, mainLog, $"[IDX] Watcher error: {e.GetException().Message}");

        watcher.EnableRaisingEvents = true;
        Log(serial, mainLog, $"[IDX] Watching: {vaultPath}");

        // キャンセルまで監視継続（guest_close / プロセス終了で抜ける）
        ct.WaitHandle.WaitOne();
        watcher.Dispose();
        Log(serial, mainLog, $"[IDX] Watch stopped: {vaultPath}");
    }
    catch (Exception ex) { Log(serial, mainLog, $"[IDX] Watcher start error: {ex.Message}"); }
}

// Vault フォルダをスキャンしてノートインデックスを構築する（バックグラウンド実行）
static void BuildVaultIndex(string vaultPath, string dbPath, string[] extraExcluded, bool exDot, bool exUnderscore, bool exIde, bool includeFolderNames, SerialPort? serial, StreamWriter? mainLog)
{
    string logDir       = Path.Combine(Path.GetDirectoryName(dbPath) ?? @"C:\Log\MCP", ".log");
    string indexLogPath = Path.Combine(logDir, $"index_{DateTime.Now:yyyyMMdd_HHmmss}.log");

    try
    {
        Directory.CreateDirectory(logDir);
        using var indexLog = new StreamWriter(indexLogPath, append: false, Encoding.UTF8) { AutoFlush = true };

        void ILog(string msg)
        {
            indexLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
            Log(serial, mainLog, $"[IDX] {msg}");
        }

        ILog($"=== Vault index start: {vaultPath} ===");
        ILog($"    DB: {dbPath}");

        using var index = new NoteIndex(dbPath);

        // MCP_EXCLUDED_FOLDERS で指定されたフォルダを DB に反映（起動毎に上書き登録）
        foreach (var folder in extraExcluded)
            index.AddExcludedFolder(folder, "env:MCP_EXCLUDED_FOLDERS");

        var excludedSet = index.GetExcludedFolders()
            .Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ILog($"    Excluded: {string.Join(", ", excludedSet)}");

        // _NoCrawl ファイルがあるディレクトリを事前収集
        var noCrawlDirs = Directory.EnumerateDirectories(vaultPath, "*", SearchOption.AllDirectories)
            .Where(HasNoCrawl)
            .Select(d => Path.GetRelativePath(vaultPath, d).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (HasNoCrawl(vaultPath))
        {
            ILog("WARN: Vault ルートに _NoCrawl があります。全ノートが除外されます。");
            noCrawlDirs.Add("");
        }
        if (noCrawlDirs.Count > 0)
            ILog($"    NoCrawl: {string.Join(", ", noCrawlDirs)}");

        bool IsNoCrawled(string folder) =>
            noCrawlDirs.Any(nc => nc.Length == 0 ||
                folder.Equals(nc, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(nc + "/", StringComparison.OrdinalIgnoreCase));

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int added = 0, updated = 0, unchanged = 0, skipped = 0, ftsIndexed = 0;
        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(vaultPath, "*.md", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(vaultPath, file).Replace('\\', '/');
            string folder   = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";

            if (IsNoCrawled(folder)) { skipped++; continue; }

            bool excluded = excludedSet.Any(e =>
                folder.Equals(e, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase))
                || IsPatternExcluded(folder, exDot, exUnderscore, exIde);
            if (excluded) { skipped++; ILog($"  SKP {relative}"); continue; }

            indexedPaths.Add(relative);

            var fi      = new FileInfo(file);
            long lastMod = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();

            string? fileContent = null;
            string? fileHash    = null;
            try
            {
                fileContent = File.ReadAllText(file, Encoding.UTF8);
                fileHash    = NoteIndex.ComputeHash(fileContent);
            }
            catch (Exception ex) { ILog($"  WARN: content read failed {relative}: {ex.Message}"); }

            var entry   = new NoteEntry(
                relative,
                string.IsNullOrEmpty(folder) ? null : folder,
                Path.GetFileName(file),
                lastMod,
                now,
                fileContent,
                fileHash);

            string sizeLabel = fileContent != null ? $" {FormatSize(fileContent.Length)}" : " (no content)";
            var result = index.UpsertIfNewer(entry);
            switch (result)
            {
                case UpsertResult.Added:
                    added++;
                    if (fileContent != null) ftsIndexed++;
                    ILog($"  ADD {relative}{sizeLabel}");
                    break;
                case UpsertResult.Updated:
                    updated++;
                    if (fileContent != null) ftsIndexed++;
                    ILog($"  UPD {relative}{sizeLabel}");
                    break;
                case UpsertResult.Unchanged:
                    unchanged++;
                    break;
            }
        }

        // フォルダ名インデックス（include_folder_names: true 時）
        int foldersAdded = 0;
        if (includeFolderNames)
        {
            var allFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(vaultPath, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(vaultPath, dir).Replace('\\', '/');
                bool dirExcluded = excludedSet.Any(e =>
                    rel.Equals(e, StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase))
                    || IsPatternExcluded(rel, exDot, exUnderscore, exIde)
                    || IsNoCrawled(rel);
                if (!dirExcluded)
                    allFolders.Add(rel);
            }

            foreach (var folderPath in allFolders.OrderBy(f => f))
            {
                indexedPaths.Add(folderPath);
                string parentDir  = Path.GetDirectoryName(folderPath)?.Replace('\\', '/') ?? "";
                string folderName = Path.GetFileName(folderPath);

                // LastModified=0 固定でコンテンツ変化がなければ Unchanged になる
                var entry = new NoteEntry(
                    folderPath,
                    string.IsNullOrEmpty(parentDir) ? null : parentDir,
                    folderName,
                    LastModified: 0,
                    IndexedAt:    now,
                    Content:      folderPath,   // パス全体を検索対象にする
                    ContentHash:  NoteIndex.ComputeHash(folderPath));

                var result = index.UpsertIfNewer(entry);
                if (result == UpsertResult.Added)
                {
                    foldersAdded++;
                    ILog($"  ADD [DIR] {folderPath}");
                }
            }
        }

        int purged = index.Purge(indexedPaths);
        if (purged > 0) ILog($"  DEL {purged} stale record(s) purged");

        ILog($"=== Done: added={added} updated={updated} unchanged={unchanged} skipped={skipped} purged={purged} fts={ftsIndexed} dirs={foldersAdded} ===");
    }
    catch (Exception ex)
    {
        Log(serial, mainLog, $"[IDX] ERROR: {ex.Message}");
    }
}

// initialize レスポンスを構築して返す
static string BuildInitializeResponse(JsonNode? idNode, string version = "0.0.0")
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"]    = new JsonObject
            {
                ["tools"]     = new JsonObject(),
                ["resources"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"]    = "mcpvault",
                ["version"] = version,
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// tools/list レスポンスを構築して返す
static string BuildToolsListResponse(JsonNode? idNode)
{
    var tools = new JsonArray();

    tools.Add(JsonNode.Parse("""
        {
            "name": "list_notes",
            "description": "Lists Obsidian notes from the local index with path and last-modified timestamp. Optionally filter by folder.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "folder": {
                        "type": "string",
                        "description": "Filter by folder (relative path). Omit to list all notes."
                    }
                }
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "recent_notes",
            "description": "Returns the most recently modified Obsidian notes with their filenames and timestamps.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "count": {
                        "type": "number",
                        "description": "Number of notes to return (1–100)",
                        "default": 10
                    }
                }
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "search_notes",
            "description": "Full-text search of Obsidian notes (FTS5 trigram). Supports Japanese and English. Query must be 3 or more characters.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "query": {
                        "type": "string",
                        "description": "Search query (3 or more characters)"
                    },
                    "limit": {
                        "type": "number",
                        "description": "Maximum number of results",
                        "default": 20
                    }
                },
                "required": ["query"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "read_note",
            "description": "Reads an Obsidian note by relative path, returns its content, and updates the local index.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path to the note (e.g. 'Daily/2024-01-15.md')"
                    }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "read_notes",
            "description": "Reads multiple Obsidian notes by relative paths in a single call. Returns each note's content preceded by a path header (=== path ===). Updates the local index for each note read.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "paths": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "List of relative paths to notes (e.g. ['Daily/2024-01-15.md', 'Projects/foo.md'])"
                    }
                },
                "required": ["paths"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "update_note",
            "description": "Overwrites an Obsidian note (.md only) with the given content, creating it if absent, and updates the local index. Use append_note to add to the end, or edit_note to change part of a note.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path to the note (must end with .md)"
                    },
                    "content": {
                        "type": "string",
                        "description": "Full new content of the note"
                    },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the note from disk after writing and appends its full content to the response for verification",
                        "default": false
                    }
                },
                "required": ["path", "content"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "append_note",
            "description": "Appends content to the end of an Obsidian note (.md only), creating it if absent, and updates the local index. A newline is inserted first if the note does not end with one. With journal:true, the entry is prefixed with a local timestamp '[yyyy-MM-dd HH:mm:ss] ' and terminated with a newline.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path to the note (must end with .md)"
                    },
                    "content": {
                        "type": "string",
                        "description": "Text to append"
                    },
                    "journal": {
                        "type": "boolean",
                        "description": "If true, prefix the entry with '[yyyy-MM-dd HH:mm:ss] ' (local time) and end it with a newline",
                        "default": false
                    },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the note from disk after writing and appends its full content to the response for verification",
                        "default": false
                    }
                },
                "required": ["path", "content"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "edit_note",
            "description": "Replaces old_text with new_text in an existing Obsidian note (.md only) and updates the local index. Fails if old_text is not found, or if it matches more than once and replace_all is false — include surrounding context to make it unique.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path to the note (must end with .md)"
                    },
                    "old_text": {
                        "type": "string",
                        "description": "Exact text to find"
                    },
                    "new_text": {
                        "type": "string",
                        "description": "Replacement text (may be empty to delete old_text)"
                    },
                    "replace_all": {
                        "type": "boolean",
                        "description": "If true, replace every occurrence",
                        "default": false
                    },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the note from disk after writing and appends its full content to the response for verification",
                        "default": false
                    }
                },
                "required": ["path", "old_text", "new_text"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "rollback_note",
            "description": "Undoes the most recent write (create_note / update_note / append_note / edit_note / delete_note) to an Obsidian note. Each call goes back one more step (up to 20 per note). Undoing a write that created the note deletes it; undoing delete_note restores it. Fails if the note was changed by something else (e.g. edited in Obsidian) after that write, unless force is true. move_note is not covered.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path to the note (must end with .md)"
                    },
                    "force": {
                        "type": "boolean",
                        "description": "If true, roll back even if the note was changed externally after the last write (external changes are lost)",
                        "default": false
                    },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the note from disk after rolling back and appends its full content to the response",
                        "default": false
                    }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "create_note",
            "description": "Creates a new Obsidian note (.md). Fails if the note already exists. Optionally initializes content from a template stored in the local index.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path for the new note (must end with .md, e.g. 'Daily/2026-04-16.md')"
                    },
                    "content": {
                        "type": "string",
                        "description": "Initial content of the note. Takes precedence over template."
                    },
                    "template": {
                        "type": "string",
                        "description": "Template name to use as initial content (e.g. 'daily' → templates/daily.md). Ignored if content is specified."
                    },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the note from disk after writing and appends its full content to the response for verification",
                        "default": false
                    }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "delete_note",
            "description": "Deletes an Obsidian note from the vault and removes it from the local index.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path to the note to delete (e.g. 'Daily/2024-01-15.md')"
                    }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "move_note",
            "description": "Moves or renames an Obsidian note and updates wiki links in other notes that reference it.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "sourcePath": {
                        "type": "string",
                        "description": "Current relative path of the note"
                    },
                    "destinationPath": {
                        "type": "string",
                        "description": "New relative path of the note (must end with .md)"
                    }
                },
                "required": ["sourcePath", "destinationPath"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "list_folders",
            "description": "Lists all folders in the Obsidian vault that contain at least one note. Useful for understanding the vault structure before creating or moving notes.",
            "inputSchema": {
                "type": "object",
                "properties": {}
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "create_folder",
            "description": "Creates a new folder in the Obsidian vault. Fails if the folder already exists.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path of the folder to create (e.g. 'Projects/NewProject')"
                    }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "rename_folder",
            "description": "Renames or moves a folder in the Obsidian vault and updates all affected note paths in the local index.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "sourcePath": {
                        "type": "string",
                        "description": "Current relative path of the folder"
                    },
                    "destinationPath": {
                        "type": "string",
                        "description": "New relative path of the folder"
                    }
                },
                "required": ["sourcePath", "destinationPath"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "delete_empty_folder",
            "description": "Deletes a folder only if it is completely empty (no files or subfolders). Fails if the folder contains any entries.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path of the folder to delete"
                    }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "kcell_write",
            "description": "Writes (overwrites) a key-value pair in a named KnowledgeCell. Creates the cell and key automatically if absent. Useful for persisting notes, session state, or cross-session instructions. Use kcell_append to add to a value, or kcell_edit to change part of it.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "cell_name": {
                        "type": "string",
                        "description": "Cell name (e.g. 'quick', 'session', 'novel'). Created automatically if absent."
                    },
                    "key": {
                        "type": "string",
                        "description": "Key"
                    },
                    "value": {
                        "type": "string",
                        "description": "Value to store"
                    },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the value from the DB after writing and appends it to the response",
                        "default": false
                    }
                },
                "required": ["cell_name", "key", "value"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "kcell_append",
            "description": "Appends text to the end of a KnowledgeCell value, creating the key if absent. A newline is inserted first if the value does not end with one. With journal:true, the entry is prefixed with a local timestamp '[yyyy-MM-dd HH:mm:ss] ' and terminated with a newline.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "cell_name": { "type": "string", "description": "Cell name" },
                    "key": { "type": "string", "description": "Key" },
                    "value": { "type": "string", "description": "Text to append" },
                    "journal": {
                        "type": "boolean",
                        "description": "If true, prefix the entry with '[yyyy-MM-dd HH:mm:ss] ' (local time) and end it with a newline",
                        "default": false
                    },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the value from the DB after writing and appends it to the response",
                        "default": false
                    }
                },
                "required": ["cell_name", "key", "value"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "kcell_edit",
            "description": "Replaces old_text with new_text in an existing KnowledgeCell value. Fails if the key does not exist, old_text is not found, or it matches more than once and replace_all is false.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "cell_name": { "type": "string", "description": "Cell name" },
                    "key": { "type": "string", "description": "Key" },
                    "old_text": { "type": "string", "description": "Exact text to find" },
                    "new_text": { "type": "string", "description": "Replacement text (may be empty to delete old_text)" },
                    "replace_all": {
                        "type": "boolean",
                        "description": "If true, replace every occurrence",
                        "default": false
                    },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the value from the DB after writing and appends it to the response",
                        "default": false
                    }
                },
                "required": ["cell_name", "key", "old_text", "new_text"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "kcell_rollback",
            "description": "Undoes the most recent kcell_write / kcell_append / kcell_edit / kcell_delete on a KnowledgeCell, restoring the affected keys' previous values and timestamps. Each call goes back one more step (up to 20 per cell). Undoing a whole-cell delete restores all its keys.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "cell_name": { "type": "string", "description": "Cell name" },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, appends the restored keys' current values to the response",
                        "default": false
                    }
                },
                "required": ["cell_name"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "kcell_read",
            "description": "Reads one or all entries from a named KnowledgeCell. Omit key to retrieve all entries sorted by most recently updated. Set latest:true to return only the single most recently written entry.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "cell_name": {
                        "type": "string",
                        "description": "Cell name"
                    },
                    "key": {
                        "type": "string",
                        "description": "Key to read. Omit to read all keys in the cell."
                    },
                    "latest": {
                        "type": "boolean",
                        "description": "If true, returns only the most recently updated entry regardless of key."
                    }
                },
                "required": ["cell_name"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "kcell_delete",
            "description": "Deletes a key from a KnowledgeCell, or deletes the entire cell and all its keys if key is omitted.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "cell_name": {
                        "type": "string",
                        "description": "Cell name"
                    },
                    "key": {
                        "type": "string",
                        "description": "Key to delete. Omit to delete the entire cell."
                    }
                },
                "required": ["cell_name"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "kcell_list",
            "description": "Lists all KnowledgeCells with their key count and last updated timestamp.",
            "inputSchema": {
                "type": "object",
                "properties": {}
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "server_info",
            "description": "Returns MCPVault server information: version, build date, main vault path, guest vault status, indexed note counts, and log file path. Use this to check which vault is being operated on.",
            "inputSchema": {
                "type": "object",
                "properties": {}
            }
        }
        """));

    // ゲストVaultツール群
    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_open",
            "description": "Opens a second (guest) Vault for temporary use. Builds a full-text index (guestnotes.db) in the background. All guest_* tools operate on this vault. Only one guest vault can be open at a time; calling again replaces the current one.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "vault_path": {
                        "type": "string",
                        "description": "Absolute path to the guest vault root folder"
                    }
                },
                "required": ["vault_path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_close",
            "description": "Closes the currently open guest vault, stops its file watcher, and deletes the temporary index (guestnotes.db).",
            "inputSchema": {
                "type": "object",
                "properties": {}
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_list_notes",
            "description": "Lists notes in the guest vault with path and last-modified timestamp. Optionally filter by folder.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "folder": {
                        "type": "string",
                        "description": "Filter by folder (relative path). Omit to list all notes."
                    }
                }
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_recent_notes",
            "description": "Returns the most recently modified notes in the guest vault.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "count": {
                        "type": "number",
                        "description": "Number of notes to return (1–100)",
                        "default": 10
                    }
                }
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_search_notes",
            "description": "Full-text search of the guest vault (FTS5 trigram). Supports Japanese and English. Query must be 3 or more characters.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "query": {
                        "type": "string",
                        "description": "Search query (3 or more characters)"
                    },
                    "limit": {
                        "type": "number",
                        "description": "Maximum number of results",
                        "default": 20
                    }
                },
                "required": ["query"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_read_note",
            "description": "Reads a note from the guest vault by relative path and updates the guest index.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path to the note (e.g. 'Daily/2024-01-15.md')"
                    }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_read_notes",
            "description": "Reads multiple notes from the guest vault in a single call. Returns each note's content preceded by a path header (=== path ===).",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "paths": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "List of relative paths"
                    }
                },
                "required": ["paths"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_create_note",
            "description": "Creates a new note in the guest vault (.md only). Fails if the note already exists.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path for the new note (must end with .md)"
                    },
                    "content": {
                        "type": "string",
                        "description": "Initial content. Takes precedence over template."
                    },
                    "template": {
                        "type": "string",
                        "description": "Template name (e.g. 'daily' → templates/daily.md). Ignored if content is specified."
                    },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the note from disk after writing and appends its full content to the response for verification",
                        "default": false
                    }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_update_note",
            "description": "Overwrites a note in the guest vault (.md only), creating it if absent, and updates the guest index.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "Relative path to the note (must end with .md)" },
                    "content": { "type": "string", "description": "Full new content of the note" },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the note from disk after writing and appends its full content to the response for verification",
                        "default": false
                    }
                },
                "required": ["path", "content"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_append_note",
            "description": "Appends content to the end of a note in the guest vault (.md only), creating it if absent. With journal:true, the entry is prefixed with '[yyyy-MM-dd HH:mm:ss] ' and terminated with a newline.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "Relative path to the note (must end with .md)" },
                    "content": { "type": "string", "description": "Text to append" },
                    "journal": { "type": "boolean", "default": false },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the note from disk after writing and appends its full content to the response for verification",
                        "default": false
                    }
                },
                "required": ["path", "content"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_edit_note",
            "description": "Replaces old_text with new_text in an existing note in the guest vault. Fails if old_text is not found, or matches more than once without replace_all.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "Relative path to the note (must end with .md)" },
                    "old_text": { "type": "string", "description": "Exact text to find" },
                    "new_text": { "type": "string", "description": "Replacement text" },
                    "replace_all": { "type": "boolean", "default": false },
                    "read_back": {
                        "type": "boolean",
                        "description": "If true, re-reads the note from disk after writing and appends its full content to the response for verification",
                        "default": false
                    }
                },
                "required": ["path", "old_text", "new_text"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_rollback_note",
            "description": "Undoes the most recent write to a note in the guest vault; each call goes back one more step. History is discarded when the guest vault is closed.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "Relative path to the note (must end with .md)" },
                    "force": { "type": "boolean", "default": false },
                    "read_back": { "type": "boolean", "default": false }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_delete_note",
            "description": "Deletes a note from the guest vault and removes it from the guest index.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path to the note to delete"
                    }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_move_note",
            "description": "Moves or renames a note in the guest vault and updates wiki links in other guest notes that reference it.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "sourcePath": { "type": "string", "description": "Current relative path" },
                    "destinationPath": { "type": "string", "description": "New relative path (must end with .md)" }
                },
                "required": ["sourcePath", "destinationPath"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_list_folders",
            "description": "Lists all folders in the guest vault that contain at least one note.",
            "inputSchema": {
                "type": "object",
                "properties": {}
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_create_folder",
            "description": "Creates a new folder in the guest vault. Fails if the folder already exists.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "Relative path of the folder to create" }
                },
                "required": ["path"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_rename_folder",
            "description": "Renames or moves a folder in the guest vault and updates all affected note paths in the guest index.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "sourcePath": { "type": "string" },
                    "destinationPath": { "type": "string" }
                },
                "required": ["sourcePath", "destinationPath"]
            }
        }
        """));

    tools.Add(JsonNode.Parse("""
        {
            "name": "guest_delete_empty_folder",
            "description": "Deletes a folder in the guest vault only if it is completely empty.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "Relative path of the folder to delete" }
                },
                "required": ["path"]
            }
        }
        """));

    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject { ["tools"] = tools }
    };
    return response.ToJsonString(jsonOptions);
}

// JSON-RPC エラーレスポンスを構築して返す（ツール実行エラーを Claude に伝える）
static string BuildErrorResponse(JsonNode? idNode, string message)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = message }
            },
            ["isError"] = true,
        }
    };
    return response.ToJsonString(jsonOptions);
}

// search_notes の JSON-RPC レスポンスを構築して返す
static string BuildSearchNotesResponse(JsonNode? idNode, string? query, string dbPath, int limit)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (string.IsNullOrWhiteSpace(query))
    {
        text = "クエリが空です。";
    }
    else if (query.Length < 3)
    {
        text = $"クエリが短すぎます（{query.Length}文字）。FTS5 trigram は3文字以上必要です。";
    }
    else
    {
        try
        {
            using var index   = new NoteIndex(dbPath);
            var results = index.SearchWithSnippets(query, Math.Clamp(limit, 1, 100)).ToList();
            if (results.Count == 0)
            {
                text = $"「{query}」に一致するノートは見つかりませんでした。";
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine($"「{query}」の検索結果 {results.Count}件:");
                int i = 1;
                foreach (var (path, snippet) in results)
                    sb.AppendLine($"{i++}. {path}\n   {snippet}");
                text = sb.ToString().TrimEnd();
            }
        }
        catch (Exception ex)
        {
            text = $"検索エラー: {ex.Message}";
        }
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// delete_note の JSON-RPC レスポンスを構築して返す（ファイル削除 + インデックス削除）
static string BuildDeleteNoteResponse(JsonNode? idNode, string? relativePath, string vaultPath, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (string.IsNullOrWhiteSpace(relativePath))
    {
        text = "path が指定されていません。";
    }
    else
    {
        try
        {
            if (ResolveVaultPath(relativePath, vaultPath, out string fullPath) is { } pathErr)
            {
                text = pathErr;
            }
            else if (!File.Exists(fullPath))
            {
                text = $"ノートが見つかりません: {relativePath}";
            }
            else
            {
                // rollback_note 用に削除前の内容を退避（.md のみ）
                bool    isMd = relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                string? prev = isMd ? File.ReadAllText(fullPath, Encoding.UTF8) : null;
                File.Delete(fullPath);
                using var index = new NoteIndex(dbPath);
                index.Delete(relativePath.Replace('\\', '/'));
                if (isMd) RecordHistory(dbPath, relativePath, "delete", prev, null);
                text = $"削除しました: {relativePath}";
            }
        }
        catch (Exception ex)
        {
            text = $"エラー: {ex.Message}";
        }
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// move_note の JSON-RPC レスポンスを構築して返す（ファイル移動 + インデックス更新）
static string BuildMoveNoteResponse(JsonNode? idNode, string? srcPath, string? dstPath, string vaultPath, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(dstPath))
    {
        text = "sourcePath と destinationPath の両方が必要です。";
    }
    else if (!dstPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    {
        text = $".md 以外への移動は禁止されています: {dstPath}";
    }
    else
    {
        try
        {
            string? srcErr = ResolveVaultPath(srcPath, vaultPath, out string fullSrc);
            string? dstErr = ResolveVaultPath(dstPath, vaultPath, out string fullDst);

            if ((srcErr ?? dstErr) is { } pathErr)
            {
                text = pathErr;
            }
            else if (!File.Exists(fullSrc))
            {
                text = $"移動元が見つかりません: {srcPath}";
            }
            else if (File.Exists(fullDst))
            {
                text = $"移動先がすでに存在します: {dstPath}";
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullDst)!);
                File.Move(fullSrc, fullDst);

                string normSrc = srcPath.Replace('\\', '/');
                string normDst = dstPath.Replace('\\', '/');

                using var index = new NoteIndex(dbPath);
                index.Delete(normSrc);

                string content = File.ReadAllText(fullDst, Encoding.UTF8);
                UpsertNote(normDst, fullDst, content, dbPath);

                int linkUpdates = File.Exists(dbPath) ? UpdateWikiLinksForNote(normSrc, normDst, vaultPath, dbPath) : 0;
                text = $"移動しました: {srcPath} → {dstPath}" +
                       (linkUpdates > 0 ? $"（リンク修正: {linkUpdates}件）" : "");
            }
        }
        catch (Exception ex)
        {
            text = $"エラー: {ex.Message}";
        }
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// list_folders の JSON-RPC レスポンスを構築して返す
static string BuildListFoldersResponse(JsonNode? idNode, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    try
    {
        using var index = new NoteIndex(dbPath);
        var folders = index.ListFolders().ToList();
        if (folders.Count == 0)
        {
            text = "フォルダが見つかりませんでした。";
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"フォルダ一覧 {folders.Count}件:");
            foreach (var f in folders)
                sb.AppendLine(f);
            text = sb.ToString().TrimEnd();
        }
    }
    catch (Exception ex)
    {
        text = $"エラー: {ex.Message}";
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// create_folder の JSON-RPC レスポンスを構築して返す（フォルダ作成）
static string BuildCreateFolderResponse(JsonNode? idNode, string? relativePath, string vaultPath, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (string.IsNullOrWhiteSpace(relativePath))
    {
        text = "path が指定されていません。";
    }
    else
    {
        try
        {
            if (ResolveVaultPath(relativePath, vaultPath, out string fullPath) is { } pathErr)
            {
                text = pathErr;
            }
            else if (Directory.Exists(fullPath))
            {
                text = $"フォルダがすでに存在します: {relativePath}";
            }
            else
            {
                Directory.CreateDirectory(fullPath);

                string norm      = relativePath.Replace('\\', '/');
                string parentDir = Path.GetDirectoryName(norm)?.Replace('\\', '/') ?? "";
                string folderName = Path.GetFileName(norm);
                long   now       = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var entry = new NoteEntry(
                    norm,
                    string.IsNullOrEmpty(parentDir) ? null : parentDir,
                    folderName,
                    LastModified: 0,
                    IndexedAt:    now,
                    Content:      norm,
                    ContentHash:  NoteIndex.ComputeHash(norm));
                using var index = new NoteIndex(dbPath);
                index.Upsert(entry);

                text = $"作成しました: {relativePath}";
            }
        }
        catch (Exception ex)
        {
            text = $"エラー: {ex.Message}";
        }
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// rename_folder の JSON-RPC レスポンスを構築して返す（フォルダ移動 + インデックス一括更新）
static string BuildRenameFolderResponse(JsonNode? idNode, string? srcPath, string? dstPath, string vaultPath, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(dstPath))
    {
        text = "sourcePath と destinationPath の両方が必要です。";
    }
    else
    {
        try
        {
            string? srcErr = ResolveVaultPath(srcPath, vaultPath, out string fullSrc);
            string? dstErr = ResolveVaultPath(dstPath, vaultPath, out string fullDst);

            if ((srcErr ?? dstErr) is { } pathErr)
            {
                text = pathErr;
            }
            else if (!Directory.Exists(fullSrc))
            {
                text = $"フォルダが見つかりません: {srcPath}";
            }
            else if (Directory.Exists(fullDst) || File.Exists(fullDst))
            {
                text = $"移動先がすでに存在します: {dstPath}";
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullDst)!);
                Directory.Move(fullSrc, fullDst);

                string normSrc = srcPath.Replace('\\', '/');
                string normDst = dstPath.Replace('\\', '/');

                int updated = 0;
                if (File.Exists(dbPath))
                {
                    using var index = new NoteIndex(dbPath);
                    updated = index.RenameFolder(normSrc, normDst);
                }
                int linkUpdates = File.Exists(dbPath) ? UpdateWikiLinksForFolder(normSrc, normDst, vaultPath, dbPath) : 0;
                string linkLabel = linkUpdates > 0 ? $"、リンク修正: {linkUpdates}件" : "";
                text = $"移動しました: {srcPath} → {dstPath}（インデックス更新: {updated}件{linkLabel}）";
            }
        }
        catch (Exception ex)
        {
            text = $"エラー: {ex.Message}";
        }
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// delete_empty_folder の JSON-RPC レスポンスを構築して返す（空フォルダのみ削除）
static string BuildDeleteEmptyFolderResponse(JsonNode? idNode, string? relativePath, string vaultPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (string.IsNullOrWhiteSpace(relativePath))
    {
        text = "path が指定されていません。";
    }
    else
    {
        try
        {
            if (ResolveVaultPath(relativePath, vaultPath, out string fullPath) is { } pathErr)
            {
                text = pathErr;
            }
            else if (!Directory.Exists(fullPath))
            {
                text = $"フォルダが見つかりません: {relativePath}";
            }
            else if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                text = $"フォルダが空ではありません: {relativePath}";
            }
            else
            {
                Directory.Delete(fullPath);
                text = $"削除しました: {relativePath}";
            }
        }
        catch (Exception ex)
        {
            text = $"エラー: {ex.Message}";
        }
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// px_create_note の JSON-RPC レスポンスを構築して返す（新規ノート作成 + インデックス更新）
static string BuildCreateNoteResponse(JsonNode? idNode, string? relativePath, string? content,
    string? templateName, bool readBack, string vaultPath, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (string.IsNullOrWhiteSpace(relativePath))
    {
        text = "path が指定されていません。";
    }
    else if (!relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    {
        text = $".md 以外のファイルへの作成は禁止されています: {relativePath}";
    }
    else
    {
        try
        {
            if (ResolveVaultPath(relativePath, vaultPath, out string fullPath) is { } pathErr)
            {
                text = pathErr;
            }
            else if (File.Exists(fullPath))
            {
                text = $"ノートがすでに存在します: {relativePath}";
            }
            else
            {
                // コンテンツ決定: content > template > 空文字
                string body = content ?? ResolveTemplate(templateName, vaultPath, dbPath) ?? "";

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, body, Encoding.UTF8);
                UpsertNote(relativePath, fullPath, body, dbPath);
                RecordHistory(dbPath, relativePath, "create", null, body);

                string tmplLabel = content != null ? "" :
                                   templateName != null ? $" (template: {templateName})" : "";
                text = WithReadBack($"作成しました{tmplLabel}: {relativePath} ({FormatSize(body.Length)})",
                    readBack, relativePath, fullPath);
            }
        }
        catch (Exception ex)
        {
            text = $"エラー: {ex.Message}";
        }
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// テンプレート名からコンテンツを解決する（DB 優先 → ファイルフォールバック）
static string? ResolveTemplate(string? templateName, string vaultPath, string dbPath)
{
    if (string.IsNullOrWhiteSpace(templateName)) return null;

    // テンプレートパスの正規化: "daily" → "templates/daily.md"
    string tmplRelPath = templateName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        ? templateName.Replace('\\', '/')
        : $"templates/{templateName}.md";

    // DB から取得（インデックス済みであれば）
    try
    {
        if (File.Exists(dbPath))
        {
            using var index = new NoteIndex(dbPath);
            var entry = index.Get(tmplRelPath);
            if (entry?.Content != null) return entry.Content;
        }
    }
    catch { }

    // フォールバック: ファイルから直接読む
    try
    {
        if (ResolveVaultPath(tmplRelPath, vaultPath, out string fullPath) is null && File.Exists(fullPath))
            return File.ReadAllText(fullPath, Encoding.UTF8);
    }
    catch { }

    return null;
}

// パストラバーサル防止: 相対パスをフルパスに展開して Vault 配下かチェック。OK なら null を返し fullPath を設定する
// （../ や絶対パスで Vault 外を指す場合はエラー。メイン／ゲストの区別がつくようエラーに Vault ルートを含める）
static string? ResolveVaultPath(string relativePath, string vaultPath, out string fullPath)
{
    fullPath         = Path.GetFullPath(Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    string vaultFull = Path.GetFullPath(vaultPath);
    return fullPath.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        ? null
        : $"Vault 外へのアクセスは禁止されています（Vault: {vaultFull}）: {relativePath}";
}

// 書き込み系ツール共通の path 検証（.md 限定・Vault 外アクセス禁止）。OK なら null を返し fullPath を設定する
static string? ValidateWritableNotePath(string? relativePath, string vaultPath, out string fullPath)
{
    fullPath = "";
    if (string.IsNullOrWhiteSpace(relativePath))
        return "path が指定されていません。";
    if (!relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        return $".md 以外のファイルへの書き込みは禁止されています: {relativePath}";
    return ResolveVaultPath(relativePath, vaultPath, out fullPath);
}

// append_note / kcell_append 共通: src の末尾に付け足す文字列を作る
// 既存内容が改行で終わっていなければ区切りの改行を入れ、journal=true なら [yyyy-MM-dd HH:mm:ss] を付けて改行で終える
static string BuildAppendEntry(string src, string content, bool journal)
{
    string lineEnding = src.Contains("\r\n") ? "\r\n" : "\n";
    string entry      = journal ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}" : content;
    if (src.Length > 0 && !src.EndsWith('\n'))
        entry = lineEnding + entry;
    if (journal && !entry.EndsWith('\n'))
        entry += lineEnding;
    return entry;
}

// edit_note / kcell_edit 共通: old_text → new_text の置換。OK なら null を返し result/count を設定する
static string? ReplaceText(string src, string oldText, string newText, bool replaceAll, out string result, out int count)
{
    result = src;
    count  = (src.Length - src.Replace(oldText, "").Length) / oldText.Length;
    if (count == 0)
        return $"old_text が見つかりませんでした: 「{oldText[..Math.Min(oldText.Length, 40)]}…」";
    if (count > 1 && !replaceAll)
        return $"old_text が {count} 箇所に一致しました。一意になるよう前後の文脈を含めるか、replace_all=true を指定してください。";
    result = src.Replace(oldText, newText);
    return null;
}

// read_back=true の場合、書き込み完了メッセージの後ろにディスクから読み直したノート内容を付ける
static string WithReadBack(string message, bool readBack, string relativePath, string fullPath) =>
    readBack ? $"{message}\n\n=== {relativePath} ===\n{File.ReadAllText(fullPath, Encoding.UTF8)}" : message;

// テキスト 1 件の tools/call レスポンスを構築する
static string BuildTextResponse(JsonNode? idNode, string text)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// update_note: ノートを上書き（存在しなければ作成）+ インデックス更新
static string BuildUpdateNoteResponse(JsonNode? idNode, string? relativePath, string? content,
    bool readBack, string vaultPath, string dbPath)
{
    string text;
    try
    {
        string? err = ValidateWritableNotePath(relativePath, vaultPath, out string fullPath);
        if (err != null)
            text = err;
        else if (content is null)
            text = "content が指定されていません。";
        else
        {
            string? prev = File.Exists(fullPath) ? File.ReadAllText(fullPath, Encoding.UTF8) : null;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, Encoding.UTF8);
            UpsertNote(relativePath!, fullPath, content, dbPath);
            RecordHistory(dbPath, relativePath!, "update", prev, content);
            text = WithReadBack($"上書きしました: {relativePath} ({FormatSize(content.Length)})",
                readBack, relativePath!, fullPath);
        }
    }
    catch (Exception ex)
    {
        text = $"エラー: {ex.Message}";
    }
    return BuildTextResponse(idNode, text);
}

// append_note: ノート末尾に追記（存在しなければ作成）+ インデックス更新
// journal=true の場合は先頭に [yyyy-MM-dd HH:mm:ss] を付けて 1 行として追記する
static string BuildAppendNoteResponse(JsonNode? idNode, string? relativePath, string? content, bool journal,
    bool readBack, string vaultPath, string dbPath)
{
    string text;
    try
    {
        string? err = ValidateWritableNotePath(relativePath, vaultPath, out string fullPath);
        if (err != null)
            text = err;
        else if (content is null)
            text = "content が指定されていません。";
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            bool   existed    = File.Exists(fullPath);
            string src        = existed ? File.ReadAllText(fullPath, Encoding.UTF8) : "";
            string entry      = BuildAppendEntry(src, content, journal);

            File.AppendAllText(fullPath, entry, Encoding.UTF8);
            string written = src + entry;
            UpsertNote(relativePath!, fullPath, written, dbPath);
            RecordHistory(dbPath, relativePath!, "append", existed ? src : null, written);
            text = WithReadBack($"{(journal ? "ジャーナル追記" : "追記")}しました: {relativePath} ({FormatSize(written.Length)})",
                readBack, relativePath!, fullPath);
        }
    }
    catch (Exception ex)
    {
        text = $"エラー: {ex.Message}";
    }
    return BuildTextResponse(idNode, text);
}

// edit_note: old_text → new_text の文字列置換（ノートが存在しなければエラー）+ インデックス更新
static string BuildEditNoteResponse(JsonNode? idNode, string? relativePath, string? oldText, string? newText,
    bool replaceAll, bool readBack, string vaultPath, string dbPath)
{
    string text;
    try
    {
        string? err = ValidateWritableNotePath(relativePath, vaultPath, out string fullPath);
        if (err != null)
            text = err;
        else if (string.IsNullOrEmpty(oldText))
            text = "old_text が指定されていません。";
        else if (newText is null)
            text = "new_text が指定されていません。";
        else if (!File.Exists(fullPath))
            text = $"ノートが存在しません: {relativePath}";
        else
        {
            string  src        = File.ReadAllText(fullPath, Encoding.UTF8);
            string? replaceErr = ReplaceText(src, oldText, newText, replaceAll, out string written, out int count);
            if (replaceErr != null)
                text = replaceErr;
            else
            {
                File.WriteAllText(fullPath, written, Encoding.UTF8);
                UpsertNote(relativePath!, fullPath, written, dbPath);
                RecordHistory(dbPath, relativePath!, "edit", src, written);
                text = WithReadBack($"置換しました ({count}箇所): {relativePath} ({FormatSize(written.Length)})",
                    readBack, relativePath!, fullPath);
            }
        }
    }
    catch (Exception ex)
    {
        text = $"エラー: {ex.Message}";
    }
    return BuildTextResponse(idNode, text);
}

// rollback_note 用の書き込み履歴を記録する（書き込み成功後に呼ぶ）
// prevContent = null: 書き込み前はファイルなし / afterContent = null: 書き込み後はファイルなし
static void RecordHistory(string dbPath, string relativePath, string op, string? prevContent, string? afterContent)
{
    using var index = new NoteIndex(dbPath);
    index.AddHistory(relativePath.Replace('\\', '/'), op, prevContent,
        afterContent is null ? null : NoteIndex.ComputeHash(afterContent));
}

// rollback_note: 直前の書き込みを 1 回分取り消す（呼ぶたびに 1 段ずつ戻る）
static string BuildRollbackNoteResponse(JsonNode? idNode, string? relativePath, bool force, bool readBack,
    string vaultPath, string dbPath)
{
    string text;
    try
    {
        string? err = ValidateWritableNotePath(relativePath, vaultPath, out string fullPath);
        if (err != null)
        {
            text = err;
        }
        else
        {
            string    normPath = relativePath!.Replace('\\', '/');
            using var index    = new NoteIndex(dbPath);
            var       hist     = index.GetLatestHistory(normPath);
            string?   curHash  = File.Exists(fullPath) ? NoteIndex.ComputeHash(File.ReadAllText(fullPath, Encoding.UTF8)) : null;

            if (hist is null)
            {
                text = $"ロールバックできる履歴がありません: {relativePath}";
            }
            else if (curHash != hist.AfterHash && !force)
            {
                text = $"{hist.Op} の後にノートが変更されています（Obsidian での編集など）。force=true で強制的に戻せます: {relativePath}";
            }
            else
            {
                if (hist.PrevContent is null)
                {
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    index.Delete(normPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllText(fullPath, hist.PrevContent, Encoding.UTF8);
                    UpsertNote(normPath, fullPath, hist.PrevContent, dbPath);
                }
                index.DeleteHistory(hist.Id);

                string msg = $"ロールバックしました（{hist.Op} を取り消し）: {relativePath}（残り履歴 {index.CountHistory(normPath)} 件）";
                text = hist.PrevContent is null
                    ? msg + "\n書き込み前はノートが存在しなかったため、削除しました。"
                    : WithReadBack(msg, readBack, relativePath, fullPath);
            }
        }
    }
    catch (Exception ex)
    {
        text = $"エラー: {ex.Message}";
    }
    return BuildTextResponse(idNode, text);
}

// ノートのインデックス更新ヘルパー（書き込み後の共通処理）
static void UpsertNote(string relativePath, string fullPath, string content, string dbPath)
{
    var    fi      = new FileInfo(fullPath);
    long   lastMod = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
    long   now     = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string norm    = relativePath.Replace('\\', '/');
    string folder  = Path.GetDirectoryName(norm)?.Replace('\\', '/') ?? "";
    string hash    = NoteIndex.ComputeHash(content);

    var entry = new NoteEntry(
        norm,
        string.IsNullOrEmpty(folder) ? null : folder,
        Path.GetFileName(norm),
        lastMod, now, content, hash);

    using var index = new NoteIndex(dbPath);
    index.Upsert(entry);
}

// [[wikilink]] を別名に置換するヘルパー（エイリアス・見出し参照を保持）
// [[oldLink]], [[oldLink|alias]], [[oldLink#heading]], ![[oldLink]] 等に対応
// [[oldLinkExtended]] のような部分一致はスキップ
static string ReplaceWikiLink(string content, string oldLink, string newLink)
{
    string pattern = @"(!?\[\[)" + Regex.Escape(oldLink) + @"((?=[#|\]])[^\]\n]*)?\]\]";
    return Regex.Replace(content, pattern,
        m => m.Groups[1].Value + newLink + (m.Groups[2].Success ? m.Groups[2].Value : "") + "]]");
}

// move_note 後に Vault 全体の [[wikilink]] を更新する（ノート単体移動用）
static int UpdateWikiLinksForNote(string oldRelPath, string newRelPath, string vaultPath, string dbPath)
{
    string oldStem      = Path.GetFileNameWithoutExtension(oldRelPath);
    string newStem      = Path.GetFileNameWithoutExtension(newRelPath);
    string oldPathNoExt = oldRelPath[..^3];   // ".md" 除去
    string newPathNoExt = newRelPath[..^3];

    if (oldPathNoExt.Equals(newPathNoExt, StringComparison.OrdinalIgnoreCase)) return 0;

    List<string> paths;
    using (var index = new NoteIndex(dbPath))
        paths = index.ListPaths().ToList();

    int updatedCount = 0;
    foreach (var relativePath in paths)
    {
        if (relativePath.Equals(newRelPath, StringComparison.OrdinalIgnoreCase)) continue;

        // インデックスに Vault 外のパスが紛れていても書き換えない
        if (ResolveVaultPath(relativePath, vaultPath, out string fullPath) != null || !File.Exists(fullPath)) continue;

        try
        {
            string content = File.ReadAllText(fullPath, Encoding.UTF8);
            string updated = ReplaceWikiLink(content, oldPathNoExt, newPathNoExt);
            if (oldStem != newStem)
                updated = ReplaceWikiLink(updated, oldStem, newStem);

            if (updated != content)
            {
                File.WriteAllText(fullPath, updated, Encoding.UTF8);
                UpsertNote(relativePath, fullPath, updated, dbPath);
                updatedCount++;
            }
        }
        catch { /* 個別ファイルのエラーは無視して継続 */ }
    }
    return updatedCount;
}

// rename_folder 後に Vault 全体の [[wikilink]] を更新する（フォルダ移動用）
static int UpdateWikiLinksForFolder(string oldFolderPath, string newFolderPath, string vaultPath, string dbPath)
{
    string oldPrefix = oldFolderPath + "/";
    string newPrefix = newFolderPath + "/";

    string pattern = @"(!?\[\[)" + Regex.Escape(oldPrefix) + @"([^\]\n]*\]\])";

    List<string> paths;
    using (var index = new NoteIndex(dbPath))
        paths = index.ListPaths().ToList();

    int updatedCount = 0;
    foreach (var relativePath in paths)
    {
        // インデックスに Vault 外のパスが紛れていても書き換えない
        if (ResolveVaultPath(relativePath, vaultPath, out string fullPath) != null || !File.Exists(fullPath)) continue;

        try
        {
            string content = File.ReadAllText(fullPath, Encoding.UTF8);
            string updated = Regex.Replace(content, pattern,
                m => m.Groups[1].Value + newPrefix + m.Groups[2].Value);

            if (updated != content)
            {
                File.WriteAllText(fullPath, updated, Encoding.UTF8);
                UpsertNote(relativePath, fullPath, updated, dbPath);
                updatedCount++;
            }
        }
        catch { /* 個別ファイルのエラーは無視して継続 */ }
    }
    return updatedCount;
}

// px_read_note の JSON-RPC レスポンスを構築して返す（ノート読み込み + インデックス更新）
static string BuildReadNoteResponse(JsonNode? idNode, string? relativePath, string vaultPath, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (string.IsNullOrWhiteSpace(relativePath))
    {
        text = "path が指定されていません。";
    }
    else
    {
        try
        {
            if (ResolveVaultPath(relativePath, vaultPath, out string fullPath) is { } pathErr)
            {
                text = pathErr;
            }
            else if (!File.Exists(fullPath))
            {
                text = $"ノートが見つかりません: {relativePath}";
            }
            else
            {
                string content  = File.ReadAllText(fullPath, Encoding.UTF8);
                var    fi       = new FileInfo(fullPath);
                long   lastMod  = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                long   now      = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string normPath = relativePath.Replace('\\', '/');
                string folder   = Path.GetDirectoryName(normPath)?.Replace('\\', '/') ?? "";
                string hash     = NoteIndex.ComputeHash(content);

                var entry = new NoteEntry(
                    normPath,
                    string.IsNullOrEmpty(folder) ? null : folder,
                    Path.GetFileName(normPath),
                    lastMod, now, content, hash);

                using var index = new NoteIndex(dbPath);
                index.Upsert(entry);

                text = content;
            }
        }
        catch (Exception ex)
        {
            text = $"エラー: {ex.Message}";
        }
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// px_read_multiple_notes の JSON-RPC レスポンスを構築して返す（複数ノート一括読み込み + インデックス更新）
static string BuildReadMultipleNotesResponse(JsonNode? idNode, string[] paths, string vaultPath, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (paths.Length == 0)
    {
        text = "paths が指定されていません。";
    }
    else
    {
        var sb = new StringBuilder();
        foreach (var relativePath in paths)
        {
            sb.AppendLine($"=== {relativePath} ===");
            try
            {
                if (ResolveVaultPath(relativePath, vaultPath, out string fullPath) is { } pathErr)
                {
                    sb.AppendLine($"({pathErr})");
                }
                else if (!File.Exists(fullPath))
                {
                    sb.AppendLine($"(ノートが見つかりません: {relativePath})");
                }
                else
                {
                    string content  = File.ReadAllText(fullPath, Encoding.UTF8);
                    var    fi       = new FileInfo(fullPath);
                    long   lastMod  = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                    long   now      = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string normPath = relativePath.Replace('\\', '/');
                    string folder   = Path.GetDirectoryName(normPath)?.Replace('\\', '/') ?? "";
                    string hash     = NoteIndex.ComputeHash(content);

                    var entry = new NoteEntry(
                        normPath,
                        string.IsNullOrEmpty(folder) ? null : folder,
                        Path.GetFileName(normPath),
                        lastMod, now, content, hash);

                    using var index = new NoteIndex(dbPath);
                    index.Upsert(entry);

                    sb.AppendLine(content);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(エラー: {ex.Message})");
            }
            sb.AppendLine();
        }
        text = sb.ToString().TrimEnd();
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// list_notes の JSON-RPC レスポンスを構築して返す
static string BuildListNotesResponse(JsonNode? idNode, string? folder, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    try
    {
        using var index = new NoteIndex(dbPath);
        var notes = index.List(folder).ToList();
        if (notes.Count == 0)
        {
            text = folder != null
                ? $"フォルダ「{folder}」にノートは見つかりませんでした。"
                : "ノートが見つかりませんでした。";
        }
        else
        {
            var sb = new StringBuilder();
            string header = folder != null
                ? $"フォルダ「{folder}」のノート {notes.Count}件:"
                : $"全ノート {notes.Count}件:";
            sb.AppendLine(header);
            foreach (var n in notes)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(n.LastModified).ToLocalTime();
                sb.AppendLine($"{n.RelativePath}  ({dt:yyyy-MM-dd HH:mm:ss})");
            }
            text = sb.ToString().TrimEnd();
        }
    }
    catch (Exception ex)
    {
        text = $"エラー: {ex.Message}";
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// recent_notes の JSON-RPC レスポンスを構築して返す
static string BuildRecentNotesResponse(JsonNode? idNode, int count, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    try
    {
        using var index = new NoteIndex(dbPath);
        var results = index.RecentNotes(Math.Clamp(count, 1, 100)).ToList();
        if (results.Count == 0)
        {
            text = "ノートが見つかりませんでした。";
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"最近更新された {results.Count}件のノート:");
            int i = 1;
            foreach (var (path, _, lastMod) in results)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(lastMod).ToLocalTime();
                sb.AppendLine($"{i++}. {path}  ({dt:yyyy-MM-dd HH:mm:ss})");
            }
            text = sb.ToString().TrimEnd();
        }
    }
    catch (Exception ex)
    {
        text = $"エラー: {ex.Message}";
    }

    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// resources/list をインデックスから構築して JSON-RPC レスポンスを返す
static string? BuildResourcesListResponse(JsonNode? idNode, string dbPath)
{
    try
    {
        using var index = new NoteIndex(dbPath);
        var notes = index.List();

        var resources = new JsonArray();
        foreach (var note in notes)
        {
            resources.Add(new JsonObject
            {
                ["uri"]      = $"obsidian://notes/{note.RelativePath}",
                ["name"]     = note.Filename,
                ["mimeType"] = "text/markdown",
            });
        }

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = idNode?.DeepClone(),
            ["result"]  = new JsonObject { ["resources"] = resources },
        };

        var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return response.ToJsonString(jsonOptions);
    }
    catch { return null; }
}

// _NoCrawl ファイルの存在確認
static bool HasNoCrawl(string dirPath) =>
    File.Exists(Path.Combine(dirPath, "_NoCrawl"));

// Watcher 用: ファイルのフルパスから Vault ルートまでの祖先に _NoCrawl があるか確認
static bool IsUnderNoCrawl(string fullFilePath, string vaultRoot)
{
    string dir       = Path.GetDirectoryName(fullFilePath)!;
    string vaultFull = Path.GetFullPath(vaultRoot);
    while (true)
    {
        if (HasNoCrawl(dir)) return true;
        if (dir.Equals(vaultFull, StringComparison.OrdinalIgnoreCase)) break;
        string? parent = Path.GetDirectoryName(dir);
        if (parent == null || parent.Equals(dir, StringComparison.OrdinalIgnoreCase)) break;
        dir = parent;
    }
    return false;
}

// フォルダパスの各セグメントをパターンで検査し、除外対象かどうかを返す
static bool IsPatternExcluded(string folder, bool exDot, bool exUnderscore, bool exIde)
{
    if (string.IsNullOrEmpty(folder)) return false;
    foreach (var seg in folder.Split('/'))
    {
        if (exDot        && seg.StartsWith('.'))  return true;
        if (exUnderscore && seg.StartsWith('_'))  return true;
        if (exIde        && IsIdeFolderName(seg)) return true;
    }
    return false;

    static bool IsIdeFolderName(string name) =>
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".git",         StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".vs",          StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".vscode",      StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".idea",        StringComparison.OrdinalIgnoreCase) ||
        name.Equals("__pycache__",  StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".gradle",      StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".mvn",         StringComparison.OrdinalIgnoreCase);
}

// read_back=true の場合、kcell 書き込み完了メッセージの後ろに DB から読み直した値を付ける
static string WithKcellReadBack(string message, bool readBack, KnowledgeCell kc, string cellName, IEnumerable<string> keys)
{
    if (!readBack) return message;
    var sb = new StringBuilder(message);
    foreach (var key in keys)
        sb.Append($"\n\n=== [{cellName}] {key} ===\n{kc.Get(cellName, key)?.Value ?? "(存在しません)"}");
    return sb.ToString();
}

// kcell_write: キーの値を上書き（存在しなければ作成）
static string BuildKcellWriteResponse(JsonNode? idNode, string? cellName, string? key, string? value, bool readBack, string dbPath)
{
    string text;
    if (string.IsNullOrWhiteSpace(cellName) || string.IsNullOrWhiteSpace(key) || value is null)
    {
        text = "cell_name, key, value はすべて必須です。";
    }
    else
    {
        try
        {
            using var kc = new KnowledgeCell(dbPath);
            kc.Write(cellName, key, value);
            text = WithKcellReadBack($"書き込みました: [{cellName}] {key}", readBack, kc, cellName, [key]);
        }
        catch (Exception ex) { text = $"エラー: {ex.Message}"; }
    }
    return BuildTextResponse(idNode, text);
}

// kcell_append: キーの値の末尾に追記（存在しなければ作成）。journal の扱いは append_note と同じ
static string BuildKcellAppendResponse(JsonNode? idNode, string? cellName, string? key, string? value, bool journal,
    bool readBack, string dbPath)
{
    string text;
    if (string.IsNullOrWhiteSpace(cellName) || string.IsNullOrWhiteSpace(key) || value is null)
    {
        text = "cell_name, key, value はすべて必須です。";
    }
    else
    {
        try
        {
            using var kc  = new KnowledgeCell(dbPath);
            string    src = kc.Get(cellName, key)?.Value ?? "";
            kc.Write(cellName, key, src + BuildAppendEntry(src, value, journal), "append");
            text = WithKcellReadBack($"{(journal ? "ジャーナル追記" : "追記")}しました: [{cellName}] {key}",
                readBack, kc, cellName, [key]);
        }
        catch (Exception ex) { text = $"エラー: {ex.Message}"; }
    }
    return BuildTextResponse(idNode, text);
}

// kcell_edit: キーの値の old_text → new_text 置換（キーが存在しなければエラー）。一致判定は edit_note と同じ
static string BuildKcellEditResponse(JsonNode? idNode, string? cellName, string? key, string? oldText, string? newText,
    bool replaceAll, bool readBack, string dbPath)
{
    string text;
    if (string.IsNullOrWhiteSpace(cellName) || string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(oldText) || newText is null)
    {
        text = "cell_name, key, old_text, new_text はすべて必須です。";
    }
    else
    {
        try
        {
            using var kc  = new KnowledgeCell(dbPath);
            var       cur = kc.Get(cellName, key);
            if (cur is null)
                text = $"[{cellName}] キー「{key}」は存在しません。";
            else if (ReplaceText(cur.Value.Value, oldText, newText, replaceAll, out string written, out int count) is { } err)
                text = err;
            else
            {
                kc.Write(cellName, key, written, "edit");
                text = WithKcellReadBack($"置換しました ({count}箇所): [{cellName}] {key}", readBack, kc, cellName, [key]);
            }
        }
        catch (Exception ex) { text = $"エラー: {ex.Message}"; }
    }
    return BuildTextResponse(idNode, text);
}

// kcell_rollback: セルへの直前の操作（write / append / edit / delete）を 1 回分取り消す
static string BuildKcellRollbackResponse(JsonNode? idNode, string? cellName, bool readBack, string dbPath)
{
    string text;
    if (string.IsNullOrWhiteSpace(cellName))
    {
        text = "cell_name は必須です。";
    }
    else
    {
        try
        {
            using var kc     = new KnowledgeCell(dbPath);
            var       result = kc.Rollback(cellName);
            if (result is not { } r)
                text = $"ロールバックできる履歴がありません: [{cellName}]";
            else
                text = WithKcellReadBack(
                    $"ロールバックしました（{r.Op} を取り消し）: [{cellName}] {string.Join(", ", r.Keys)}（残り履歴 {r.Remaining} 件）",
                    readBack, kc, cellName, r.Keys);
        }
        catch (Exception ex) { text = $"エラー: {ex.Message}"; }
    }
    return BuildTextResponse(idNode, text);
}

// kcell_read の JSON-RPC レスポンスを構築して返す
static string BuildKcellReadResponse(JsonNode? idNode, string? cellName, string? key, bool latest, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    string text;
    if (string.IsNullOrWhiteSpace(cellName))
    {
        text = "cell_name は必須です。";
    }
    else
    {
        try
        {
            string? normKey = string.IsNullOrWhiteSpace(key) ? null : key;
            using var kc = new KnowledgeCell(dbPath);

            if (latest)
            {
                // 最終更新エントリを1件だけ返す（key指定は無視）
                var entries = kc.Read(cellName, key: null).ToList();
                if (entries.Count == 0)
                    text = $"[{cellName}] にエントリはありません。";
                else
                {
                    var (k, v, ua) = entries[0];
                    var dt = DateTimeOffset.FromUnixTimeSeconds(ua).ToLocalTime();
                    text = $"[{cellName}] 最新: {k} = {v}  ({dt:yyyy-MM-dd HH:mm:ss})";
                }
            }
            else
            {
                var entries = kc.Read(cellName, normKey).ToList();
                if (entries.Count == 0)
                {
                    text = normKey != null
                        ? $"[{cellName}] キー「{normKey}」は存在しません。"
                        : $"[{cellName}] にエントリはありません。";
                }
                else if (normKey != null)
                {
                    // 特定キー指定の場合は値のみ返す
                    text = entries[0].Value;
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"[{cellName}] {entries.Count}件:");
                    foreach (var (k, v, ua) in entries)
                    {
                        var dt = DateTimeOffset.FromUnixTimeSeconds(ua).ToLocalTime();
                        sb.AppendLine($"  {k} = {v}  ({dt:yyyy-MM-dd HH:mm:ss})");
                    }
                    text = sb.ToString().TrimEnd();
                }
            }
        }
        catch (Exception ex) { text = $"エラー: {ex.Message}"; }
    }
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// kcell_delete の JSON-RPC レスポンスを構築して返す
static string BuildKcellDeleteResponse(JsonNode? idNode, string? cellName, string? key, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    string text;
    if (string.IsNullOrWhiteSpace(cellName))
    {
        text = "cell_name は必須です。";
    }
    else
    {
        try
        {
            string? normKey = string.IsNullOrWhiteSpace(key) ? null : key;
            using var kc = new KnowledgeCell(dbPath);
            int deleted = kc.Delete(cellName, normKey);
            if (deleted == 0)
                text = normKey != null
                    ? $"[{cellName}] キー「{normKey}」は存在しません。"
                    : $"[{cellName}] は存在しません。";
            else
                text = normKey != null
                    ? $"削除しました: [{cellName}] {normKey}"
                    : $"削除しました: [{cellName}]（{deleted}件）";
        }
        catch (Exception ex) { text = $"エラー: {ex.Message}"; }
    }
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// server_info: バージョン・ビルド日・Vault の場所・インデックス件数などを返す
static string BuildServerInfoResponse(JsonNode? idNode, string version, string buildDate,
    string? vaultPath, string dbPath, string? guestVaultPath, string guestDbPath, string? logPath)
{
    // インデックス済みノート数（DB 未作成ならインデックス構築前）
    static string CountNotes(string db)
    {
        if (!File.Exists(db)) return "インデックス未作成";
        try
        {
            using var index = new NoteIndex(db);
            // include_folder_names 有効時はフォルダも notes に入るため .md のみ数える
            return $"{index.ListPaths().Count(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))} 件";
        }
        catch (Exception ex) { return $"取得エラー: {ex.Message}"; }
    }

    var sb = new StringBuilder();
    sb.AppendLine($"MCPVault {version} (build {buildDate})");
    sb.AppendLine($"メインVault: {vaultPath ?? "(未設定)"}");
    sb.AppendLine($"  インデックス済みノート: {CountNotes(dbPath)}");
    sb.AppendLine($"  データフォルダ: {Path.GetDirectoryName(dbPath)}");
    if (guestVaultPath != null)
    {
        sb.AppendLine($"ゲストVault: {guestVaultPath}");
        sb.AppendLine($"  インデックス済みノート: {CountNotes(guestDbPath)}");
    }
    else
    {
        sb.AppendLine("ゲストVault: (未オープン)");
    }
    sb.Append($"ログファイル: {logPath ?? "(無効)"}");
    return BuildTextResponse(idNode, sb.ToString());
}

// kcell_list の JSON-RPC レスポンスを構築して返す
static string BuildKcellListResponse(JsonNode? idNode, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    string text;
    try
    {
        using var kc = new KnowledgeCell(dbPath);
        var cells = kc.List().ToList();
        if (cells.Count == 0)
        {
            text = "KnowledgeCellにデータはありません。";
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"KnowledgeCells {cells.Count}セル:");
            foreach (var (cn, cnt, lu) in cells)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(lu).ToLocalTime();
                sb.AppendLine($"  {cn}  ({cnt}件, 最終更新: {dt:yyyy-MM-dd HH:mm:ss})");
            }
            text = sb.ToString().TrimEnd();
        }
    }
    catch (Exception ex) { text = $"エラー: {ex.Message}"; }
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = idNode?.DeepClone(),
        ["result"]  = new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }
        }
    };
    return response.ToJsonString(jsonOptions);
}

// Vault ルートに生成する README.md の内容を返す
static string BuildWelcomeReadme() => """
    # ようこそ MCPVault へ

    MCPVault は、Claude（AI）があなたの Obsidian Vault に直接アクセスするための MCP サーバーです。

    ## できること

    - ノートの一覧・検索（日本語全文検索対応）
    - ノートの作成・読み込み・更新・削除
    - フォルダの作成・移動・削除
    - Vault の変更をリアルタイムでインデックスに反映

    ## しくみ

    標準入出力（stdin/stdout）で JSON-RPC 2.0 メッセージをやりとりし、
    Claude Desktop などの MCP クライアントから Vault を操作できます。
    ノートは SQLite（FTS5）でインデックス化されており、高速な全文検索が可能です。

    設定ファイルは `.MCPVault/mcp_config.json` にあります。

    ---
    *このファイルは MCPVault が自動生成しました。自由に編集してかまいません。*
    """;

// .MCPVault/mcp_config.json の初期内容を返す
static string BuildDefaultConfig() => """
    {
      "exclusions": {
        "exclude_dot_folders": true,
        "exclude_underscore_folders": false,
        "exclude_ide_folders": true,
        "additional_folders": []
      },
      "indexing": {
        "include_folder_names": true
      }
    }
    """;

static class Sync
{
    public static readonly object LogLock = new();
}
