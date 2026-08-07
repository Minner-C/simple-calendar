using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 科大讯飞长语音转写服务
    /// 文档：https://www.xfyun.cn/doc/asr/lfasr/API.html
    /// 流程：上传音频 → 创建转写任务 → 轮询任务状态 → 获取结果
    /// </summary>
    public class XfyunSpeechTranscriber
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private const string BaseUrl = "https://raasr.xfyun.com/v2/api";

        private readonly XfyunSettings _settings;

        public XfyunSpeechTranscriber(XfyunSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 转写音频文件（完整流程）
        /// </summary>
        /// <param name="audioPath">音频文件路径（wav/mp3等）</param>
        /// <param name="onProgress">进度回调（0-100）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>转写文本</returns>
        public async Task<string> TranscribeAsync(
            string audioPath,
            Action<int, string>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (!_settings.IsValid)
                throw new InvalidOperationException("讯飞配置无效，请在设置中填写 AppID/APIKey/APISecret");

            if (!File.Exists(audioPath))
                throw new FileNotFoundException($"音频文件不存在: {audioPath}");

            onProgress?.Invoke(5, "准备上传音频...");

            // 1. 上传音频并创建转写任务
            string taskId = await UploadAndCreateTaskAsync(audioPath, onProgress, cancellationToken);
            Debug.WriteLine($"[Xfyun] 转写任务已创建: {taskId}");

            // 2. 轮询任务状态
            onProgress?.Invoke(50, "正在转写中...");
            await PollTaskResultAsync(taskId, onProgress, cancellationToken);

            // 3. 获取转写结果
            onProgress?.Invoke(95, "获取转写结果...");
            string result = await GetResultAsync(taskId, cancellationToken);
            onProgress?.Invoke(100, "转写完成");

            return result;
        }

        /// <summary>
        /// 上传音频文件并创建转写任务
        /// </summary>
        private async Task<string> UploadAndCreateTaskAsync(
            string audioPath,
            Action<int, string>? onProgress,
            CancellationToken ct)
        {
            string ts = GetTimestamp();
            string signa = GetSignature(ts);

            var fileInfo = new FileInfo(audioPath);
            long fileSize = fileInfo.Length;
            string fileName = Path.GetFileName(audioPath);

            // 讯飞要求分片上传，这里简化为单片上传（小于100MB）
            const int SliceSize = 10 * 1024 * 1024; // 10MB per slice
            int sliceNum = (int)Math.Ceiling((double)fileSize / SliceSize);

            onProgress?.Invoke(10, $"上传音频（{fileSize / 1024}KB，{sliceNum}片）...");

            string taskId = "";

            // 读取文件
            byte[] fileBytes = await File.ReadAllBytesAsync(audioPath, ct);

            for (int i = 0; i < sliceNum; i++)
            {
                ct.ThrowIfCancellationRequested();
                long offset = i * SliceSize;
                int currentSize = (int)Math.Min(SliceSize, fileSize - offset);
                byte[] slice = new byte[currentSize];
                Array.Copy(fileBytes, offset, slice, 0, currentSize);

                using var content = new MultipartFormDataContent();
                content.Add(new StringContent(_settings.AppId), "appId");
                content.Add(new StringContent(signa), "signa");
                content.Add(new StringContent(ts), "ts");
                content.Add(new StringContent(fileName), "fileName");
                content.Add(new StringContent(fileSize.ToString()), "fileSize");
                content.Add(new StringContent((i + 1).ToString()), "sliceId");
                content.Add(new StringContent(sliceNum.ToString()), "sliceNum");
                content.Add(new StringContent(i == 0 ? "1" : "0"), "isTaskId"); // 第一片创建任务
                if (i > 0) content.Add(new StringContent(taskId), "taskId");

                using var fileContent = new ByteArrayContent(slice);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(fileContent, "file", fileName);

                var uploadUrl = $"{BaseUrl}/upload";
                var response = await _http.PostAsync(uploadUrl, content, ct);
                var respJson = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"上传失败 HTTP {response.StatusCode}: {respJson}");

                using var doc = JsonDocument.Parse(respJson);
                var root = doc.RootElement;
                int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                string desc = root.TryGetProperty("descInfo", out var d) ? d.GetString() ?? "" : "";

                if (code != 0)
                    throw new InvalidOperationException($"上传分片 {i + 1} 失败: {desc}");

                if (i == 0)
                {
                    taskId = root.GetProperty("content").GetString() ?? "";
                    if (string.IsNullOrEmpty(taskId))
                        throw new InvalidOperationException("未获取到任务ID");
                }

                onProgress?.Invoke(10 + (int)(30.0 * (i + 1) / sliceNum), $"上传分片 {i + 1}/{sliceNum}");
            }

            return taskId;
        }

        /// <summary>
        /// 轮询任务状态直到完成
        /// </summary>
        private async Task PollTaskResultAsync(
            string taskId,
            Action<int, string>? onProgress,
            CancellationToken ct)
        {
            string ts = GetTimestamp();
            string signa = GetSignature(ts);

            int maxRetry = 120; // 最多等待10分钟（5秒×120）
            for (int i = 0; i < maxRetry; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5000, ct);

                var url = $"{BaseUrl}/getProgress?appId={_settings.AppId}&taskId={taskId}&signa={Uri.EscapeDataString(signa)}&ts={ts}";
                var resp = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(resp);
                var root = doc.RootElement;

                int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                if (code != 0)
                    throw new InvalidOperationException($"查询进度失败: {root.GetProperty("descInfo").GetString()}");

                int status = root.GetProperty("content").GetInt32();
                // 0=未开始 1=转写中 2=转写结束 3=转写失败
                if (status == 2)
                {
                    onProgress?.Invoke(90, "转写完成，获取结果...");
                    return;
                }
                if (status == 3)
                    throw new InvalidOperationException("转写任务失败");

                onProgress?.Invoke(50 + i / 2, $"转写中... ({i * 5}秒)");
            }
            throw new TimeoutException("转写超时（超过10分钟）");
        }

        /// <summary>
        /// 获取转写结果
        /// </summary>
        private async Task<string> GetResultAsync(string taskId, CancellationToken ct)
        {
            string ts = GetTimestamp();
            string signa = GetSignature(ts);

            var url = $"{BaseUrl}/getResult?appId={_settings.AppId}&taskId={taskId}&signa={Uri.EscapeDataString(signa)}&ts={ts}";
            var resp = await _http.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            if (code != 0)
                throw new InvalidOperationException($"获取结果失败: {root.GetProperty("descInfo").GetString()}");

            string content = root.GetProperty("content").GetString() ?? "";

            // 讯飞返回的是 JSON 数组，每个元素含 onebest 字段
            try
            {
                using var resultDoc = JsonDocument.Parse(content);
                if (resultDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var item in resultDoc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("onebest", out var text))
                            sb.Append(text.GetString());
                    }
                    return sb.ToString();
                }
            }
            catch { }

            return content;
        }

        /// <summary>获取时间戳（秒）</summary>
        private static string GetTimestamp()
        {
            return ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString();
        }

        /// <summary>生成签名：hmac-sha1(apiSecret, appId+ts) → base64</summary>
        private string GetSignature(string ts)
        {
            string baseStr = _settings.AppId + ts;
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(_settings.ApiSecret));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseStr));
            return Convert.ToBase64String(hash);
        }
    }
}
