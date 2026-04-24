using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// MCPVault - スタンドアロン MCP サーバー
// Claude ↔ stdin/stdout ↔ [MCPVault] ↔ Obsidian Vault（ファイルシステム直接アクセス）

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
// セミコロン区切りで除外フォルダを追加可能 例: "Tools3;Private"
string[] EXTRA_EXCLUDED = (Environment.GetEnvironmentVariable("MCP_EXCLUDED_FOLDERS") ?? "")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
    errorLogWriter.WriteLine($"=== MCPVault 1.0.0  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    errorLogWriter.WriteLine($"    Tools({toolNames.Length}): {string.Join(", ", toolNames)}");
}
catch (Exception ex) { Console.Error.WriteLine($"Error log open failed: {ex.Message}"); }

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
            response = BuildInitializeResponse(id);
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

            response = toolName switch
            {
                "list_notes" => File.Exists(DB_PATH)
                    ? BuildListNotesResponse(id, pargs?["folder"]?.GetValue<string>(), DB_PATH)
                    : BuildErrorResponse(id, "インデックスが準備できていません。しばらく待ってから再試行してください。"),

                "search_notes" or "search_vault" => File.Exists(DB_PATH)
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
                        pargs?["append"]?.GetValue<bool>()               ?? false,
                        pargs?["create_if_not_exists"]?.GetValue<bool>() ?? true,
                        pargs?["mode"]?.GetValue<string>(),
                        pargs?["old_text"]?.GetValue<string>(),
                        pargs?["replace_all"]?.GetValue<bool>()          ?? false,
                        pargs?["anchor"]?.GetValue<string>(),
                        pargs?["insert_after"]?.GetValue<bool>()         ?? true,
                        VAULT_PATH, DB_PATH)
                    : BuildErrorResponse(id, "OBSIDIAN_VAULT_PATH が設定されていません。"),

                "create_note" => VAULT_PATH != null
                    ? BuildCreateNoteResponse(id,
                        pargs?["path"]?.GetValue<string>(),
                        pargs?["content"]?.GetValue<string>(),
                        pargs?["template"]?.GetValue<string>(),
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

                _ => BuildErrorResponse(id, $"不明なツール: {toolName}")
            };
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
static void WatchVaultChanges(string vaultPath, string dbPath, bool exDot, bool exUnderscore, bool exIde, SerialPort? serial, StreamWriter? mainLog)
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

        // プロセス終了まで監視継続
        Thread.Sleep(Timeout.Infinite);
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
static string BuildInitializeResponse(JsonNode? idNode)
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
                ["version"] = "1.0.0",
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
            "description": "Writes content to an Obsidian note (.md only) and updates the local index. Modes: overwrite (default), append, replace (old_text→content), insert (around anchor line).",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Relative path to the note (must end with .md)"
                    },
                    "content": {
                        "type": "string",
                        "description": "Content to write / replacement text / text to insert"
                    },
                    "append": {
                        "type": "boolean",
                        "description": "If true, append to the end of the file instead of overwriting",
                        "default": false
                    },
                    "create_if_not_exists": {
                        "type": "boolean",
                        "description": "If false, returns an error when the file does not exist",
                        "default": true
                    },
                    "mode": {
                        "type": "string",
                        "description": "Write mode: 'replace' to substitute old_text with content; 'insert' to add content before/after the first line containing anchor",
                        "enum": ["replace", "insert"]
                    },
                    "old_text": {
                        "type": "string",
                        "description": "[replace mode] Text to find and replace. Error if not found."
                    },
                    "replace_all": {
                        "type": "boolean",
                        "description": "[replace mode] If true, replace all occurrences. Default: false (first only).",
                        "default": false
                    },
                    "anchor": {
                        "type": "string",
                        "description": "[insert mode] Text to search for in lines. Inserts relative to the first matching line."
                    },
                    "insert_after": {
                        "type": "boolean",
                        "description": "[insert mode] If true (default), insert after the anchor line; if false, insert before it.",
                        "default": true
                    }
                },
                "required": ["path", "content"]
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

// search_notes / search_vault の JSON-RPC レスポンスを構築して返す
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
            string fullPath  = Path.GetFullPath(Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string vaultFull = Path.GetFullPath(vaultPath);
            if (!fullPath.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                text = $"Vault 外へのアクセスは禁止されています: {relativePath}";
            }
            else if (!File.Exists(fullPath))
            {
                text = $"ノートが見つかりません: {relativePath}";
            }
            else
            {
                File.Delete(fullPath);
                using var index = new NoteIndex(dbPath);
                index.Delete(relativePath.Replace('\\', '/'));
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
            string vaultFull = Path.GetFullPath(vaultPath);
            string fullSrc   = Path.GetFullPath(Path.Combine(vaultPath, srcPath.Replace('/', Path.DirectorySeparatorChar)));
            string fullDst   = Path.GetFullPath(Path.Combine(vaultPath, dstPath.Replace('/', Path.DirectorySeparatorChar)));

            if (!fullSrc.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !fullDst.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                text = "Vault 外へのアクセスは禁止されています。";
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
            string fullPath  = Path.GetFullPath(Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string vaultFull = Path.GetFullPath(vaultPath);
            if (!fullPath.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                text = $"Vault 外へのアクセスは禁止されています: {relativePath}";
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
            string vaultFull = Path.GetFullPath(vaultPath);
            string fullSrc   = Path.GetFullPath(Path.Combine(vaultPath, srcPath.Replace('/', Path.DirectorySeparatorChar)));
            string fullDst   = Path.GetFullPath(Path.Combine(vaultPath, dstPath.Replace('/', Path.DirectorySeparatorChar)));

            if (!fullSrc.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !fullDst.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                text = "Vault 外へのアクセスは禁止されています。";
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
            string fullPath  = Path.GetFullPath(Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string vaultFull = Path.GetFullPath(vaultPath);
            if (!fullPath.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                text = "Vault 外へのアクセスは禁止されています。";
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
    string? templateName, string vaultPath, string dbPath)
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
            string fullPath  = Path.GetFullPath(Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string vaultFull = Path.GetFullPath(vaultPath);
            if (!fullPath.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                text = $"Vault 外へのアクセスは禁止されています: {relativePath}";
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

                string tmplLabel = content != null ? "" :
                                   templateName != null ? $" (template: {templateName})" : "";
                text = $"作成しました{tmplLabel}: {relativePath} ({FormatSize(body.Length)})";
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
        string fullPath = Path.Combine(vaultPath, tmplRelPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
            return File.ReadAllText(fullPath, Encoding.UTF8);
    }
    catch { }

    return null;
}

// px_update_note の JSON-RPC レスポンスを構築して返す（ノート書き込み + インデックス更新）
static string BuildUpdateNoteResponse(JsonNode? idNode, string? relativePath, string? content,
    bool append, bool createIfNotExists,
    string? mode, string? oldText, bool replaceAll, string? anchor, bool insertAfter,
    string vaultPath, string dbPath)
{
    var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string text;
    if (string.IsNullOrWhiteSpace(relativePath))
    {
        text = "path が指定されていません。";
    }
    else if (content is null)
    {
        text = "content が指定されていません。";
    }
    else if (!relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    {
        text = $".md 以外のファイルへの書き込みは禁止されています: {relativePath}";
    }
    else
    {
        try
        {
            // パストラバーサル防止: フルパスに展開して Vault 配下かチェック
            string fullPath  = Path.GetFullPath(Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string vaultFull = Path.GetFullPath(vaultPath);
            if (!fullPath.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                text = $"Vault 外へのアクセスは禁止されています: {relativePath}";
            }
            else if (!File.Exists(fullPath) && !createIfNotExists)
            {
                text = $"ノートが存在しません（create_if_not_exists=false）: {relativePath}";
            }
            else if (mode == "replace")
            {
                if (string.IsNullOrEmpty(oldText))
                {
                    text = "replace モードには old_text が必要です。";
                }
                else
                {
                    string src = File.Exists(fullPath) ? File.ReadAllText(fullPath, Encoding.UTF8) : "";
                    if (!src.Contains(oldText))
                    {
                        text = $"old_text が見つかりませんでした: 「{oldText[..Math.Min(oldText.Length, 40)]}…」";
                    }
                    else
                    {
                        string written = replaceAll
                            ? src.Replace(oldText, content)
                            : ReplaceFirst(src, oldText, content);
                        File.WriteAllText(fullPath, written, Encoding.UTF8);
                        int count = replaceAll
                            ? (src.Length - src.Replace(oldText, "").Length) / oldText.Length
                            : 1;
                        UpsertNote(relativePath, fullPath, written, dbPath);
                        text = $"置換しました ({count}箇所): {relativePath} ({FormatSize(written.Length)})";
                    }
                }
            }
            else if (mode == "insert")
            {
                if (string.IsNullOrEmpty(anchor))
                {
                    text = "insert モードには anchor が必要です。";
                }
                else
                {
                    string src = File.Exists(fullPath) ? File.ReadAllText(fullPath, Encoding.UTF8) : "";
                    string lineEnding = src.Contains("\r\n") ? "\r\n" : "\n";
                    var lines = src.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                    int idx = lines.FindIndex(l => l.Contains(anchor));
                    if (idx < 0)
                    {
                        text = $"anchor が見つかりませんでした: 「{anchor}」";
                    }
                    else
                    {
                        int insertAt = insertAfter ? idx + 1 : idx;
                        lines.Insert(insertAt, content);
                        string written = string.Join(lineEnding, lines);
                        File.WriteAllText(fullPath, written, Encoding.UTF8);
                        UpsertNote(relativePath, fullPath, written, dbPath);
                        string pos = insertAfter ? $"「{anchor}」の後" : $"「{anchor}」の前";
                        text = $"挿入しました ({pos}): {relativePath} ({FormatSize(written.Length)})";
                    }
                }
            }
            else
            {
                // 上書き / 追記モード
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                if (append && File.Exists(fullPath))
                    File.AppendAllText(fullPath, content, Encoding.UTF8);
                else
                    File.WriteAllText(fullPath, content, Encoding.UTF8);

                string written = File.ReadAllText(fullPath, Encoding.UTF8);
                UpsertNote(relativePath, fullPath, written, dbPath);
                text = $"{(append ? "追記" : "上書き")}しました: {relativePath} ({FormatSize(written.Length)})";
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

// 最初の1件のみ置換するヘルパー
static string ReplaceFirst(string src, string oldText, string newText)
{
    int pos = src.IndexOf(oldText, StringComparison.Ordinal);
    return pos < 0 ? src : src[..pos] + newText + src[(pos + oldText.Length)..];
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

        string fullPath = Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) continue;

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
        string fullPath = Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) continue;

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
            string fullPath = Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
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
                string fullPath = Path.Combine(vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
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
        "include_folder_names": false
      }
    }
    """;

static class Sync
{
    public static readonly object LogLock = new();
}
