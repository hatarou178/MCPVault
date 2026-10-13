using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// .MCPVault/mcp_config.json の内容。人間が編集し、AI は変更できない。
/// </summary>
public sealed class AppConfig
{
    // exclusions
    public string[] AdditionalFolders       = [];
    public bool     ExcludeDotFolders        = true;
    public bool     ExcludeUnderscoreFolders = false;
    public bool     ExcludeIdeFolders        = true;

    // scope: 空なら Vault 全体。フォルダの相対パスを列挙すると、その配下だけが対象になる
    public string[] ScopeMain  = [];
    public string[] ScopeGuest = [];

    // guest: read_only / read_write
    public string GuestMode = "read_only";

    // permissions
    public bool AllowDelete       = false;
    public bool AllowFolderDelete = false;

    // editing
    public int EditMaxPairs = 20;

    // http
    public bool   HttpEnabled = false;
    public int    HttpPort    = 39680;
    public string HttpToken   = "";

    public bool GuestReadOnly => !GuestMode.Equals("read_write", StringComparison.OrdinalIgnoreCase);

    public static AppConfig Load(string path)
    {
        var cfg = new AppConfig();
        if (!File.Exists(path)) return cfg;

        var json = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
        var excl = json?["exclusions"];
        cfg.AdditionalFolders       = ReadStringArray(excl?["additional_folders"]);
        cfg.ExcludeDotFolders        = excl?["exclude_dot_folders"]?.GetValue<bool>()        ?? cfg.ExcludeDotFolders;
        cfg.ExcludeUnderscoreFolders = excl?["exclude_underscore_folders"]?.GetValue<bool>() ?? cfg.ExcludeUnderscoreFolders;
        cfg.ExcludeIdeFolders        = excl?["exclude_ide_folders"]?.GetValue<bool>()        ?? cfg.ExcludeIdeFolders;

        var scope = json?["scope"];
        cfg.ScopeMain  = ReadStringArray(scope?["main"]).Select(NormalizeFolder).Where(s => s.Length > 0).ToArray();
        cfg.ScopeGuest = ReadStringArray(scope?["guest"]).Select(NormalizeFolder).Where(s => s.Length > 0).ToArray();

        cfg.GuestMode = json?["guest"]?["mode"]?.GetValue<string>() ?? cfg.GuestMode;

        var perm = json?["permissions"];
        cfg.AllowDelete       = perm?["allow_delete"]?.GetValue<bool>()        ?? cfg.AllowDelete;
        cfg.AllowFolderDelete = perm?["allow_folder_delete"]?.GetValue<bool>() ?? cfg.AllowFolderDelete;

        cfg.EditMaxPairs = json?["editing"]?["max_edit_pairs"]?.GetValue<int>() ?? cfg.EditMaxPairs;

        var http = json?["http"];
        cfg.HttpEnabled = http?["enabled"]?.GetValue<bool>() ?? cfg.HttpEnabled;
        cfg.HttpPort    = http?["port"]?.GetValue<int>()     ?? cfg.HttpPort;
        cfg.HttpToken   = http?["token"]?.GetValue<string>() ?? cfg.HttpToken;

        return cfg;
    }

    private static string[] ReadStringArray(JsonNode? node) =>
        node?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>().ToArray() ?? [];

    private static string NormalizeFolder(string s) => s.Replace('\\', '/').Trim().Trim('/');

    public static string DefaultJson => """
        {
          "exclusions": {
            "exclude_dot_folders": true,
            "exclude_underscore_folders": false,
            "exclude_ide_folders": true,
            "additional_folders": []
          },
          "scope": {
            "main": [],
            "guest": []
          },
          "guest": {
            "mode": "read_only"
          },
          "permissions": {
            "allow_delete": false,
            "allow_folder_delete": false
          },
          "editing": {
            "max_edit_pairs": 20
          },
          "http": {
            "enabled": false,
            "port": 39680,
            "token": ""
          }
        }
        """;
}

/// <summary>
/// 設定ファイルをツール呼び出しのたびに読み直す。ファイルの更新日時が変わっていなければキャッシュを返す。
/// </summary>
public sealed class ConfigProvider
{
    private readonly string _path;
    private readonly object _lock = new();
    private AppConfig _cached = new();
    private long _cachedTicks = -1;
    private readonly Action<string>? _onError;

    public ConfigProvider(string path, Action<string>? onError = null)
    {
        _path    = path;
        _onError = onError;
    }

    public string Path => _path;

    public AppConfig Current
    {
        get
        {
            lock (_lock)
            {
                long ticks = File.Exists(_path) ? new FileInfo(_path).LastWriteTimeUtc.Ticks : 0;
                if (ticks == _cachedTicks) return _cached;
                try
                {
                    _cached      = AppConfig.Load(_path);
                    _cachedTicks = ticks;
                }
                catch (Exception ex)
                {
                    _onError?.Invoke($"Config file error: {ex.Message}");
                }
                return _cached;
            }
        }
    }
}
