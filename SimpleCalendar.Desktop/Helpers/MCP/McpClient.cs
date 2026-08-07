using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleCalendar.Helpers.MCP
{
    // ============================================================
    //  MCP 配置模型（参考 workany mcp.json 格式）
    // ============================================================

    /// <summary>MCP服务器配置</summary>
    public class McpServerConfig
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "stdio"; // stdio / http / sse

        [JsonPropertyName("command")]
        public string? Command { get; set; }

        [JsonPropertyName("args")]
        public List<string>? Args { get; set; }

        [JsonPropertyName("env")]
        public Dictionary<string, string>? Env { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }

    /// <summary>MCP配置文件根结构</summary>
    public class McpConfigFile
    {
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    }

    // ============================================================
    //  MCP 工具定义（JSON-RPC 返回结构）
    // ============================================================

    public class McpToolInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("inputSchema")]
        public JsonElement? InputSchema { get; set; }
    }

    // ============================================================
    //  JSON-RPC 消息模型
    // ============================================================

    public class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("params")]
        public object? Params { get; set; }
    }

    public class JsonRpcResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public JsonElement? Id { get; set; }

        [JsonPropertyName("result")]
        public JsonElement? Result { get; set; }

        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; set; }
    }

    public class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    // ============================================================
    //  MCP 客户端：支持 stdio / sse 两种传输
    // ============================================================

    /// <summary>
    /// MCP客户端：连接MCP服务器，发现工具，调用工具
    /// 传输协议：stdio（子进程）或 sse（HTTP流式）
    /// </summary>
    public class McpClient : IDisposable
    {
        private readonly string _serverName;
        private readonly McpServerConfig _config;
        private Process? _stdioProcess;
        private StreamWriter? _stdin;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonRpcResponse>> _pending = new();
        private int _nextId = 1;
        private bool _disposed;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _initialized;

        /// <summary>从服务器发现的工具列表</summary>
        public List<McpToolInfo> Tools { get; private set; } = new();

        public bool IsConnected => _initialized && !_disposed;

        public McpClient(string serverName, McpServerConfig config)
        {
            _serverName = serverName;
            _config = config;
        }

        /// <summary>连接并初始化MCP服务器，发现可用工具</summary>
        public async Task<bool> ConnectAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                if (_initialized) return true;

                var transport = (_config.Type ?? "stdio").ToLowerInvariant();
                if (transport == "stdio")
                {
                    if (!await StartStdioAsync()) return false;
                }
                else if (transport is "sse" or "http")
                {
                    // SSE/HTTP传输：无需持久连接，按需请求
                    // 初始化时尝试连接验证
                }
                else
                {
                    Debug.WriteLine($"[MCP] {_serverName}: 不支持的传输类型 {transport}");
                    return false;
                }

                // 发送 initialize 请求
                var initParams = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "SimpleCalendar", version = "1.0" }
                };
                var initResp = await SendRequestAsync("initialize", initParams);
                if (initResp?.Error != null)
                {
                    Debug.WriteLine($"[MCP] {_serverName}: initialize 失败 - {initResp.Error.Message}");
                    return false;
                }

                // 发送 notifications/initialized 通知
                await SendNotificationAsync("notifications/initialized");

                // 发现工具
                await DiscoverToolsAsync();
                _initialized = true;
                Debug.WriteLine($"[MCP] {_serverName}: 连接成功，发现 {Tools.Count} 个工具");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MCP] {_serverName}: 连接异常 - {ex.Message}");
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>启动stdio子进程</summary>
        private async Task<bool> StartStdioAsync()
        {
            if (string.IsNullOrEmpty(_config.Command))
            {
                Debug.WriteLine($"[MCP] {_serverName}: stdio模式缺少command");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = _config.Command,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (_config.Args != null)
                foreach (var arg in _config.Args)
                    psi.ArgumentList.Add(arg);

            if (_config.Env != null)
                foreach (var kv in _config.Env)
                    psi.Environment[kv.Key] = kv.Value;

            try
            {
                _stdioProcess = new Process { StartInfo = psi };
                _stdioProcess.Start();

                _stdin = _stdioProcess.StandardInput;
                _stdin.AutoFlush = true;

                // 后台读取stdout
                _ = Task.Run(ReadStdoutLoopAsync);

                // 后台读取stderr用于调试
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!_disposed && _stdioProcess != null && !_stdioProcess.HasExited)
                        {
                            var line = await _stdioProcess.StandardError.ReadLineAsync();
                            if (line != null)
                                Debug.WriteLine($"[MCP] {_serverName} stderr: {line}");
                        }
                    }
                    catch { }
                });

                await Task.Delay(100); // 给进程启动时间
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MCP] {_serverName}: 启动进程失败 - {ex.Message}");
                return false;
            }
        }

        /// <summary>持续读取stdout，分发JSON-RPC响应</summary>
        private async Task ReadStdoutLoopAsync()
        {
            try
            {
                while (!_disposed && _stdioProcess != null && !_stdioProcess.HasExited)
                {
                    var line = await _stdioProcess.StandardOutput.ReadLineAsync();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var msg = JsonSerializer.Deserialize<JsonRpcResponse>(line);
                        if (msg != null && msg.Id.HasValue)
                        {
                            var id = msg.Id.Value.GetInt32();
                            if (_pending.TryRemove(id, out var tcs))
                                tcs.TrySetResult(msg);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MCP] {_serverName}: 解析响应失败 - {ex.Message}, line={line}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MCP] {_serverName}: stdout读取异常 - {ex.Message}");
            }
        }

        /// <summary>发送JSON-RPC请求（stdio模式）</summary>
        private async Task<JsonRpcResponse?> SendRequestAsync(string method, object? parameters = null)
        {
            var transport = (_config.Type ?? "stdio").ToLowerInvariant();
            if (transport == "stdio")
            {
                return await SendStdioRequestAsync(method, parameters);
            }
            else if (transport is "sse" or "http")
            {
                return await SendHttpRequestAsync(method, parameters);
            }
            return null;
        }

        private async Task<JsonRpcResponse?> SendStdioRequestAsync(string method, object? parameters)
        {
            if (_stdin == null) return null;

            int id = Interlocked.Increment(ref _nextId) - 1;
            var req = new JsonRpcRequest { Id = id, Method = method, Params = parameters };
            var json = JsonSerializer.Serialize(req);

            var tcs = new TaskCompletionSource<JsonRpcResponse>();
            _pending[id] = tcs;

            await _stdin.WriteLineAsync(json);

            // 超时30秒
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            try
            {
                return await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                _pending.TryRemove(id, out _);
                Debug.WriteLine($"[MCP] {_serverName}: 请求 {method} 超时");
                return null;
            }
        }

        private async Task SendNotificationAsync(string method)
        {
            var transport = (_config.Type ?? "stdio").ToLowerInvariant();
            if (transport == "stdio" && _stdin != null)
            {
                var notif = new { jsonrpc = "2.0", method };
                await _stdin.WriteLineAsync(JsonSerializer.Serialize(notif));
            }
            // SSE/HTTP模式不需要发送通知
        }

        /// <summary>SSE/HTTP模式：发送HTTP POST请求</summary>
        private async Task<JsonRpcResponse?> SendHttpRequestAsync(string method, object? parameters)
        {
            if (string.IsNullOrEmpty(_config.Url)) return null;

            int id = Interlocked.Increment(ref _nextId) - 1;
            var req = new JsonRpcRequest { Id = id, Method = method, Params = parameters };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var content = new StringContent(JsonSerializer.Serialize(req), System.Text.Encoding.UTF8, "application/json");

            if (_config.Headers != null)
                foreach (var kv in _config.Headers)
                    http.DefaultRequestHeaders.Add(kv.Key, kv.Value);

            try
            {
                var resp = await http.PostAsync(_config.Url, content);
                var json = await resp.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<JsonRpcResponse>(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MCP] {_serverName}: HTTP请求失败 - {ex.Message}");
                return null;
            }
        }

        /// <summary>发现服务器提供的工具</summary>
        private async Task DiscoverToolsAsync()
        {
            var resp = await SendRequestAsync("tools/list");
            if (resp?.Result == null) return;

            try
            {
                if (resp.Result.Value.TryGetProperty("tools", out var toolsEl))
                {
                    Tools = JsonSerializer.Deserialize<List<McpToolInfo>>(toolsEl.GetRawText()) ?? new();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MCP] {_serverName}: 解析工具列表失败 - {ex.Message}");
            }
        }

        /// <summary>调用MCP工具</summary>
        public async Task<string> CallToolAsync(string toolName, JsonElement arguments)
        {
            var args = new
            {
                name = toolName,
                arguments = arguments.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : arguments
            };
            var resp = await SendRequestAsync("tools/call", args);

            if (resp?.Error != null)
                return JsonSerializer.Serialize(new { error = resp.Error.Message });

            if (resp?.Result == null)
                return JsonSerializer.Serialize(new { error = "空响应" });

            // MCP返回格式: { content: [{ type: "text", text: "..." }] }
            try
            {
                if (resp.Result.Value.TryGetProperty("content", out var contentEl))
                {
                    var texts = new List<string>();
                    foreach (var item in contentEl.EnumerateArray())
                    {
                        if (item.TryGetProperty("text", out var textEl))
                            texts.Add(textEl.GetString() ?? "");
                    }
                    return string.Join("\n", texts);
                }
                return resp.Result.Value.GetRawText();
            }
            catch
            {
                return resp.Result.Value.GetRawText();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_stdin != null)
                {
                    _stdin.Close();
                    _stdin = null;
                }
                if (_stdioProcess != null && !_stdioProcess.HasExited)
                {
                    _stdioProcess.Kill(true);
                    _stdioProcess.Dispose();
                    _stdioProcess = null;
                }
            }
            catch { }

            foreach (var kv in _pending)
                kv.Value.TrySetCanceled();
            _pending.Clear();
        }
    }
}
