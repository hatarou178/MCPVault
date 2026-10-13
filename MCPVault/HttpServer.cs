using System.Net;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// GlassNote などローカルアプリ向けの HTTP インターフェース。127.0.0.1 のみで待ち受け、
/// MCP と同じツール名・同じ引数を POST /tools/{name} で受ける。認証は Authorization: Bearer {token}。
/// scope・read_only・削除権限の判定は MCP 経由と同じ入口（Tools.Call）を通る。
/// </summary>
public static class HttpToolServer
{
    public static bool TryStart(ServerContext ctx, int port, string token, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "http.token が空のため HTTP インターフェースを起動しません（mcp_config.json にトークンを設定してください）";
            return false;
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            error = $"HTTP listener start failed on 127.0.0.1:{port}: {ex.Message}"
                  + (OperatingSystem.IsWindows() ? $"（必要なら管理者で: netsh http add urlacl url=http://127.0.0.1:{port}/ user=Everyone）" : "");
            return false;
        }

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleAsync(ctx, token, c));
            }
        });
        return true;
    }

    private static async Task HandleAsync(ServerContext ctx, string token, HttpListenerContext c)
    {
        var req = c.Request;
        var res = c.Response;
        try
        {
            string? auth = req.Headers["Authorization"];
            if (auth == null || !auth.Equals($"Bearer {token}", StringComparison.Ordinal))
            {
                await WriteJsonAsync(res, 401, new JsonObject { ["error"] = "unauthorized" });
                return;
            }

            string path = req.Url?.AbsolutePath ?? "/";

            if (req.HttpMethod == "GET" && path == "/tools")
            {
                await WriteJsonAsync(res, 200, new JsonObject { ["tools"] = Tools.Definitions() });
                return;
            }

            if (req.HttpMethod == "POST" && path.StartsWith("/tools/", StringComparison.Ordinal))
            {
                string name = Uri.UnescapeDataString(path["/tools/".Length..].Trim('/'));
                string body;
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                    body = await sr.ReadToEndAsync();

                JsonNode? args = null;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try { args = JsonNode.Parse(body); }
                    catch (Exception ex)
                    {
                        await WriteJsonAsync(res, 400, new JsonObject { ["error"] = $"invalid JSON: {ex.Message}" });
                        return;
                    }
                }

                ToolResult r;
                lock (Sync.ToolLock) r = Tools.Call(ctx, name, args);
                ctx.Log($"[HTTP] {name} → {(r.IsError ? "error" : "ok")}");

                await WriteJsonAsync(res, 200, new JsonObject
                {
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = r.Text } },
                    ["isError"] = r.IsError,
                });
                return;
            }

            await WriteJsonAsync(res, 404, new JsonObject { ["error"] = "not found. Use GET /tools or POST /tools/{name}" });
        }
        catch (Exception ex)
        {
            ctx.Log($"[HTTP] error: {ex.Message}");
            try { await WriteJsonAsync(res, 500, new JsonObject { ["error"] = ex.Message }); } catch { }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int status, JsonNode body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body.ToJsonString(Tools.JsonOpts));
        res.StatusCode      = status;
        res.ContentType     = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.OutputStream.Close();
    }
}
