using System;
using System.Text.Json;
using System.Threading.Tasks;
using SimpleCalendar.Helpers.MCP;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// MCP工具适配器：将MCP服务器提供的工具包装为IAgentTool
    /// 参考 workany 的 AgentPlugin 适配模式
    /// </summary>
    public class McpToolAdapter : IAgentTool
    {
        private readonly McpClient _client;
        private readonly McpToolInfo _toolInfo;
        private readonly string _serverName;

        /// <summary>带命名空间的工具名（serverName.toolName）</summary>
        public string Name => $"{_serverName}__{_toolInfo.Name}";

        /// <summary>对外显示的纯工具名（无命名空间，用于AI调用）</summary>
        public string ToolName => _toolInfo.Name;

        public string Description => $"[{_serverName}] {_toolInfo.Description}";
        public string ServerName => _serverName;

        public string ParametersSchema
        {
            get
            {
                if (_toolInfo.InputSchema == null)
                    return @"{""type"":""object"",""properties"":{}}";
                return _toolInfo.InputSchema.Value.GetRawText();
            }
        }

        public McpToolAdapter(McpClient client, McpToolInfo toolInfo, string serverName)
        {
            _client = client;
            _toolInfo = toolInfo;
            _serverName = serverName;
        }

        public string Execute(string argumentsJson)
        {
            try
            {
                var args = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                    ? JsonDocument.Parse("{}").RootElement
                    : JsonDocument.Parse(argumentsJson).RootElement;

                // MCP工具调用是异步的，但IAgentTool.Execute是同步的
                // 使用GetAwaiter().GetResult()阻塞等待（与TranscribeAudioTool相同模式）
                return _client.CallToolAsync(_toolInfo.Name, args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = $"MCP工具调用失败: {ex.Message}" });
            }
        }
    }
}
