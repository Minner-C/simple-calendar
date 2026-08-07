using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SimpleCalendar.Helpers.MCP;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// MCP服务器管理器：加载配置、连接服务器、注册工具到ToolRegistry
    /// 参考 workany 的 mcp/loader.ts 和 mcp.json 配置格式
    /// </summary>
    public static class McpServerManager
    {
        private static readonly Dictionary<string, McpClient> _clients = new();
        private static bool _loaded = false;
        private static readonly object _lock = new();

        /// <summary>配置文件路径：%AppData%\SimpleCalendar\mcp.json</summary>
        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar", "mcp.json");

        /// <summary>默认配置（内置开源MCP服务器，fetch/memory 默认启用，其余按需启用）</summary>
        private static readonly McpConfigFile _defaultConfig = new()
        {
            McpServers = new Dictionary<string, McpServerConfig>
            {
                // 网页抓取（无需API Key，开箱即用，最常用）
                ["fetch"] = new McpServerConfig
                {
                    Type = "stdio",
                    Command = "npx",
                    Args = new List<string> { "-y", "@modelcontextprotocol/server-fetch" },
                    Enabled = true  // 默认启用：网页抓取是最常用的外部能力，无需配置
                },
                // 文件系统访问（需指定允许的目录）
                ["filesystem"] = new McpServerConfig
                {
                    Type = "stdio",
                    Command = "npx",
                    Args = new List<string> { "-y", "@modelcontextprotocol/server-filesystem", "C:\\Users" },
                    Enabled = false
                },
                // GitHub（需配置 GITHUB_TOKEN）
                ["github"] = new McpServerConfig
                {
                    Type = "stdio",
                    Command = "npx",
                    Args = new List<string> { "-y", "@modelcontextprotocol/server-github" },
                    Env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "" },
                    Enabled = false
                },
                // SQLite 数据库
                ["sqlite"] = new McpServerConfig
                {
                    Type = "stdio",
                    Command = "npx",
                    Args = new List<string> { "-y", "@modelcontextprotocol/server-sqlite", "C:\\Users\\Public\\data.db" },
                    Enabled = false
                },
                // 记忆/知识图谱（无需配置，本地存储）
                ["memory"] = new McpServerConfig
                {
                    Type = "stdio",
                    Command = "npx",
                    Args = new List<string> { "-y", "@modelcontextprotocol/server-memory" },
                    Enabled = true  // 默认启用：本地记忆能力，无需任何配置
                }
            }
        };

        /// <summary>加载MCP配置文件（已有配置会合并新默认项，保留用户已设的启用状态）</summary>
        public static McpConfigFile LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var loaded = JsonSerializer.Deserialize<McpConfigFile>(json);
                    if (loaded?.McpServers == null) return _defaultConfig;

                    // 合并：对默认配置中存在但用户配置中缺失的服务器，补齐默认项
                    // （已存在的服务器保留用户的 Enabled 设置，不覆盖）
                    foreach (var kv in _defaultConfig.McpServers)
                    {
                        if (!loaded.McpServers.ContainsKey(kv.Key))
                            loaded.McpServers[kv.Key] = kv.Value;
                    }
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MCP] 加载配置失败: {ex.Message}");
            }
            return _defaultConfig;
        }

        /// <summary>保存MCP配置文件</summary>
        public static void SaveConfig(McpConfigFile config)
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MCP] 保存配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化所有已配置的MCP服务器，连接并将工具注册到ToolRegistry
        /// 应在应用启动时（或AI窗口首次打开时）调用
        /// </summary>
        public static async Task InitializeAsync()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;
            }

            var config = LoadConfig();
            foreach (var kv in config.McpServers)
            {
                var name = kv.Key;
                var serverConfig = kv.Value;
                if (!serverConfig.Enabled) continue;

                try
                {
                    var client = new McpClient(name, serverConfig);
                    bool connected = await client.ConnectAsync();
                    if (connected && client.Tools.Count > 0)
                    {
                        _clients[name] = client;
                        // 将每个MCP工具注册到ToolRegistry
                        foreach (var tool in client.Tools)
                        {
                            var adapter = new McpToolAdapter(client, tool, name);
                            ToolRegistry.Register(adapter);
                            Debug.WriteLine($"[MCP] 注册工具: {adapter.Name} - {tool.Description}");
                        }
                    }
                    else if (connected && client.Tools.Count == 0)
                    {
                        Debug.WriteLine($"[MCP] {name}: 连接成功但无工具");
                        client.Dispose();
                    }
                    else
                    {
                        Debug.WriteLine($"[MCP] {name}: 连接失败");
                        client.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MCP] {name}: 初始化异常 - {ex.Message}");
                }
            }
        }

        /// <summary>获取已连接的MCP服务器列表</summary>
        public static List<(string name, int toolCount, bool connected)> GetServerStatus()
        {
            var result = new List<(string, int, bool)>();
            var config = LoadConfig();
            foreach (var kv in config.McpServers)
            {
                bool connected = _clients.TryGetValue(kv.Key, out var client) && client.IsConnected;
                int toolCount = connected ? client.Tools.Count : 0;
                result.Add((kv.Key, toolCount, connected));
            }
            return result;
        }

        /// <summary>重新加载所有MCP服务器（先断开旧的，再连接新的）</summary>
        public static async Task ReloadAsync()
        {
            DisconnectAll();
            lock (_lock) { _loaded = false; }
            await InitializeAsync();
        }

        /// <summary>断开所有MCP服务器连接</summary>
        public static void DisconnectAll()
        {
            foreach (var kv in _clients.Values)
            {
                try { kv.Dispose(); } catch { }
            }
            _clients.Clear();
        }

        /// <summary>获取MCP工具的命名空间前缀（serverName__）</summary>
        public static string GetToolNamespace(string serverName) => $"{serverName}__";

        /// <summary>判断工具名是否来自MCP</summary>
        public static bool IsMcpTool(string toolName) => toolName.Contains("__");
    }
}
