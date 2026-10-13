using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

// MCPVault 2.0 - スタンドアロン MCP サーバー
// Claude ↔ stdin/stdout ↔ [MCPVault] ↔ SQLite（正）⇄ Obsidian Vault の md
//
// ツール定義とディスパッチは Tools.cs、target ごとの実体は Targets.cs（Vault）/ KcellTarget.cs、
// SQLite は VaultStore.cs、設定は Config.cs、HTTP の口は HttpServer.cs。

var asm = Assembly.GetExecutingAssembly();
string appVersion = asm.GetName().Version?.ToString(3) ?? "0.0.0";
string buildDate  = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "unknown";

// UTF-8 強制（Windows デフォルトは UTF-8 でないため MCP の JSON が壊れる）
Console.InputEncoding  = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

// コマンドライン引数: --vault <パス> / --log [ファイル名]
string? logPath       = null;
string? vaultArg      = null;
bool    logUseDefault = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--vault", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        vaultArg = args[++i];
    else if (args[i].Equals("--log", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            logPath = args[++i];
        else
            logUseDefault = true;
    }
}

string vaultPath = vaultArg ?? Environment.GetEnvironmentVariable("OBSIDIAN_VAULT_PATH")
    ?? (OperatingSystem.IsWindows()
        ? @"C:\Vault"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Vault"));
vaultPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultPath));

string mcpVaultDir    = Path.Combine(vaultPath, ".MCPVault");
string dbPath         = Path.Combine(mcpVaultDir, "notes.db");
string kcDbPath       = Path.Combine(mcpVaultDir, "KnowledgeCell.db");
string guestDbPath    = Path.Combine(mcpVaultDir, "guestnotes.db");
string configFilePath = Path.Combine(mcpVaultDir, "mcp_config.json");
string logDir         = Path.Combine(mcpVaultDir, ".log");

// 環境変数の除外フォルダ（セミコロン区切り）
string[] envExcluded = (Environment.GetEnvironmentVariable("MCP_EXCLUDED_FOLDERS") ?? "")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// 初回セットアップ: .MCPVault とデフォルト設定
if (!Directory.Exists(mcpVaultDir))
{
    try
    {
        Directory.CreateDirectory(mcpVaultDir);
        File.WriteAllText(configFilePath, AppConfig.DefaultJson, Importer.Utf8NoBom);
    }
    catch (Exception ex) { Console.Error.WriteLine($"MCPVault init error: {ex.Message}"); }
}

// Vault ルートに README.md がなければ生成
if (Directory.Exists(vaultPath))
{
    string readmePath = Path.Combine(vaultPath, "README.md");
    if (!File.Exists(readmePath))
    {
        try { File.WriteAllText(readmePath, Importer.ToDisk(WelcomeReadme), Importer.Utf8NoBom); }
        catch (Exception ex) { Console.Error.WriteLine($"README.md init error: {ex.Message}"); }
    }
}

if (logUseDefault)
    logPath = Path.Combine(logDir, $"mcp_vault_{DateTime.Now:yyyyMMdd_HHmmss}.log");

using var logger = new Logger(logPath, Path.Combine(logDir, "mcpvault_error.log"));
logger.Log($"=== MCPVault started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
logger.ErrorRaw($"=== MCPVault {appVersion} (build {buildDate})  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
logger.ErrorRaw($"    Tools({Tools.Names().Length}): {string.Join(", ", Tools.Names())}");

var configProvider = new ConfigProvider(configFilePath, msg => logger.Error(msg));
var cfg = configProvider.Current;

// KnowledgeCell DB を初期化
try { using var kc = new KnowledgeCell(kcDbPath); }
catch (Exception ex) { logger.Error($"KnowledgeCell init error: {ex.Message}"); }

// 前回のゲスト DB の残骸を削除
try { VaultStore.DeleteDatabase(guestDbPath); }
catch (Exception ex) { logger.Error($"Guest DB cleanup error: {ex.Message}"); }

var ctx = new ServerContext
{
    AppVersion  = appVersion,
    BuildDate   = buildDate,
    VaultPath   = vaultPath,
    DbPath      = dbPath,
    KcDbPath    = kcDbPath,
    GuestDbPath = guestDbPath,
    LogPath     = logPath,
    Config      = configProvider,
    Log         = logger.Log,
    EnvExcluded = envExcluded,
};

// ウォッチャーを先に起動し、並行してフルスキャンを行う
ctx.StartVault = (vp, db, ct) =>
{
    _ = Task.Run(() => WatchVaultChanges(vp, db, configProvider, logger, ct), CancellationToken.None);
    _ = Task.Run(() => Importer.FullScan(vp, db, configProvider.Current, ctx.ExtraExcluded, logger.Log), CancellationToken.None);
};

if (Directory.Exists(vaultPath))
    ctx.StartVault(vaultPath, dbPath, CancellationToken.None);
else
    logger.Error($"[IDX] WARN: Vault が見つかりません: {vaultPath}");

// HTTP インターフェース（設定で有効な場合のみ）
if (cfg.HttpEnabled)
{
    if (HttpToolServer.TryStart(ctx, cfg.HttpPort, cfg.HttpToken, out string httpErr))
    {
        ctx.HttpRunning = true;
        ctx.HttpPort    = cfg.HttpPort;
        logger.Log($"[HTTP] listening on 127.0.0.1:{cfg.HttpPort}");
    }
    else
        logger.Error($"[HTTP] {httpErr}");
}

logger.Log("[MCPVault] ready");

// メインループ（JSON-RPC 2.0）
string? input;
while ((input = await Console.In.ReadLineAsync()) != null)
{
    if (string.IsNullOrWhiteSpace(input)) continue;
    logger.Log($"[REQ] {input}");

    string?   response        = null;
    string?   currentToolName = null;
    JsonNode? id              = null;
    try
    {
        var req        = JsonNode.Parse(input);
        string? method = req?["method"]?.GetValue<string>();
        id             = req?["id"];

        if (method == "initialize")
        {
            response = Rpc(id, new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"]    = new JsonObject { ["tools"] = new JsonObject(), ["resources"] = new JsonObject() },
                ["serverInfo"]      = new JsonObject { ["name"] = "mcpvault", ["version"] = appVersion },
            });
        }
        else if (method?.StartsWith("notifications/") == true)
        {
            logger.Log($"[MCPVault] notification: {method}");
        }
        else if (method == "tools/list")
        {
            response = Rpc(id, new JsonObject { ["tools"] = Tools.Definitions() });
        }
        else if (method == "tools/call")
        {
            currentToolName = req?["params"]?["name"]?.GetValue<string>();
            var pargs       = req?["params"]?["arguments"];
            logger.Log($"[MCPVault] tools/call {currentToolName}");

            ToolResult r;
            lock (Sync.ToolLock) r = Tools.Call(ctx, currentToolName, pargs);

            var result = new JsonObject
            {
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = r.Text } },
            };
            if (r.IsError) result["isError"] = true;
            response = Rpc(id, result);
            if (r.IsError) logger.Error($"[ERR] {currentToolName ?? "?"}: {Fmt.Head(r.Text.Split('\n')[0], 300)}");
        }
        else if (method == "resources/list")
        {
            response = Rpc(id, new JsonObject { ["resources"] = BuildResources(dbPath, configProvider.Current.ScopeMain) });
        }
        else if (id != null)
        {
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = id.DeepClone(),
                ["error"]   = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" },
            }.ToJsonString(Tools.JsonOpts);
        }
    }
    catch (Exception ex)
    {
        logger.Error($"[RPC] {ex.Message}");
        if (id != null)
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = id.DeepClone(),
                ["error"]   = new JsonObject { ["code"] = -32603, ["message"] = ex.Message },
            }.ToJsonString(Tools.JsonOpts);
    }

    if (response != null)
    {
        await Console.Out.WriteLineAsync(response);
        await Console.Out.FlushAsync();
        logger.Log($"[OUT] {response}");
    }
}

logger.Log($"=== MCPVault stopped {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

// -----------------------------------------------------------------------------
// ローカル関数
// -----------------------------------------------------------------------------

static string Rpc(JsonNode? id, JsonNode result) => new JsonObject
{
    ["jsonrpc"] = "2.0",
    ["id"]      = id?.DeepClone(),
    ["result"]  = result,
}.ToJsonString(Tools.JsonOpts);

static JsonArray BuildResources(string dbPath, string[] scope)
{
    var resources = new JsonArray();
    if (!File.Exists(dbPath)) return resources;
    try
    {
        using var store = new VaultStore(dbPath);
        foreach (var n in store.ListInfos(null, scope))
            resources.Add(new JsonObject
            {
                ["uri"]      = $"obsidian://notes/{n.RelativePath}",
                ["name"]     = n.Filename,
                ["mimeType"] = "text/markdown",
            });
    }
    catch { }
    return resources;
}

// Vault の .md を監視して差分取込する（500ms デバウンス）
static void WatchVaultChanges(string vaultPath, string dbPath, ConfigProvider config, Logger logger, CancellationToken ct)
{
    try
    {
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

        void ImportOne(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath)) return;
                if (Importer.IsUnderNoCrawl(fullPath, vaultPath)) return;

                using var store = new VaultStore(dbPath);
                string relative = Importer.ToRelative(vaultPath, fullPath);
                string folder   = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
                if (Importer.IsExcludedFolder(folder, store.GetExcludedFolders(), config.Current)) return;

                var (result, rec) = Importer.ImportFile(store, vaultPath, fullPath);
                if (result != ImportResult.Unchanged)
                    logger.Log($"[IDX] {result.ToString().ToUpper()} (watch) {relative} v{rec?.Version}");
            }
            catch (Exception ex) { logger.Log($"[IDX] Watch import error: {ex.Message}"); }
        }

        void RemoveOne(string fullPath)
        {
            try
            {
                using var store = new VaultStore(dbPath);
                string relative = Importer.ToRelative(vaultPath, fullPath);
                if (store.Get(relative) == null) return;
                store.Delete(relative);
                logger.Log($"[IDX] DEL (watch) {relative}");
            }
            catch (Exception ex) { logger.Log($"[IDX] Watch delete error: {ex.Message}"); }
        }

        var watcher = new FileSystemWatcher(vaultPath)
        {
            Filter                = "*.md",
            IncludeSubdirectories = true,
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            InternalBufferSize    = 65536,
        };

        watcher.Created += (_, e) => Debounce(e.FullPath, () => ImportOne(e.FullPath));
        watcher.Changed += (_, e) => Debounce(e.FullPath, () => ImportOne(e.FullPath));
        watcher.Deleted += (_, e) => Debounce(e.FullPath, () => RemoveOne(e.FullPath));
        watcher.Renamed += (_, e) => { RemoveOne(e.OldFullPath); Debounce(e.FullPath, () => ImportOne(e.FullPath)); };
        watcher.Error   += (_, e) => logger.Log($"[IDX] Watcher error: {e.GetException().Message}");

        watcher.EnableRaisingEvents = true;
        logger.Log($"[IDX] Watching: {vaultPath}");

        ct.WaitHandle.WaitOne();
        watcher.Dispose();
        logger.Log($"[IDX] Watch stopped: {vaultPath}");
    }
    catch (Exception ex) { logger.Log($"[IDX] Watcher start error: {ex.Message}"); }
}

// -----------------------------------------------------------------------------
// 定数・補助クラス
// -----------------------------------------------------------------------------

partial class Program
{
    const string WelcomeReadme = """
        # ようこそ MCPVault へ

        MCPVault は、Claude（AI）があなたの Obsidian Vault に直接アクセスするための MCP サーバーです。

        ## できること

        - ノートの一覧・検索（日本語全文検索対応）
        - ノートの作成・読み込み・更新・削除・ロールバック
        - フォルダの作成・移動・削除
        - Vault の変更をリアルタイムで取り込み

        ## しくみ

        標準入出力（stdin/stdout）で JSON-RPC 2.0 メッセージをやりとりし、
        Claude Desktop などの MCP クライアントから Vault を操作できます。
        ノートは SQLite（FTS5）に取り込まれ、高速な全文検索と version 管理ができます。

        設定ファイルは `.MCPVault/mcp_config.json` にあります（scope・ゲスト Vault のモード・削除権限など）。

        ---
        *このファイルは MCPVault が自動生成しました。自由に編集してかまいません。*
        """;
}

/// <summary>詳細ログ（--log 指定時）とエラーログ（常時）。複数スレッドから呼ばれるため lock で保護する。</summary>
public sealed class Logger : IDisposable
{
    private readonly StreamWriter? _main;
    private readonly StreamWriter? _err;

    public Logger(string? mainLogPath, string errorLogPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(errorLogPath)!);
            _err = new StreamWriter(errorLogPath, append: true, Importer.Utf8NoBom) { AutoFlush = true };
        }
        catch (Exception ex) { Console.Error.WriteLine($"Error log open failed: {ex.Message}"); }

        if (mainLogPath != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(mainLogPath)!);
                _main = new StreamWriter(mainLogPath, append: true, Importer.Utf8NoBom) { AutoFlush = true };
            }
            catch (Exception ex) { Console.Error.WriteLine($"Log file error: {ex.Message}"); }
        }
    }

    public void Log(string message)
    {
        if (_main == null) return;
        lock (Sync.LogLock)
        {
            try { _main.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}"); } catch { }
        }
    }

    public void Error(string message)
    {
        Log(message);
        ErrorRaw($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
    }

    public void ErrorRaw(string line)
    {
        if (_err == null) return;
        lock (Sync.LogLock)
        {
            try { _err.WriteLine(line); } catch { }
        }
    }

    public void Dispose()
    {
        lock (Sync.LogLock)
        {
            _main?.Dispose();
            _err?.Dispose();
        }
    }
}
