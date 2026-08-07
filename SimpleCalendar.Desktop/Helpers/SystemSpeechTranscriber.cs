using System;
using System.Diagnostics;
using System.IO;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// Windows 系统自带语音识别（基于 System.Speech）
    /// 免费、无需 API Key，但准确率一般，仅支持短时识别
    /// 适用于未配置讯飞API时的回退方案
    /// </summary>
    public class SystemSpeechTranscriber : IDisposable
    {
        private bool _disposed;

        /// <summary>
        /// 识别 WAV 音频文件为文字
        /// </summary>
        public Task<string> TranscribeAsync(string audioPath)
        {
            if (!File.Exists(audioPath))
                throw new FileNotFoundException($"音频文件不存在: {audioPath}");

            return Task.Run(() =>
            {
                try
                {
                    // 检查中文语音识别是否可用
                    var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers();
                    RecognizerInfo? chineseRecognizer = null;
                    foreach (var r in installedRecognizers)
                    {
                        if (r.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                        {
                            chineseRecognizer = r;
                            break;
                        }
                    }

                    // 没有中文识别器，尝试用英文
                    if (chineseRecognizer == null)
                    {
                        foreach (var r in installedRecognizers)
                        {
                            if (r.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                            {
                                chineseRecognizer = r;
                                break;
                            }
                        }
                    }

                    if (chineseRecognizer == null)
                    {
                        // 没有任何识别器
                        return "[系统未安装语音识别引擎，请配置科大讯飞API，或在Windows设置→时间和语言→语音中添加语音包]";
                    }

                    using var engine = new SpeechRecognitionEngine(chineseRecognizer);
                    var dictationGrammar = new DictationGrammar();
                    engine.LoadGrammar(dictationGrammar);

                    // 设置音频输入
                    engine.SetInputToWaveFile(audioPath);

                    // 初始化识别结果
                    var resultBuilder = new System.Text.StringBuilder();
                    var doneEvent = new ManualResetEvent(false);

                    engine.RecognizeCompleted += (s, e) =>
                    {
                        try
                        {
                            if (e.Result != null)
                            {
                                resultBuilder.Append(e.Result.Text);
                            }
                            if (e.Error != null)
                            {
                                Debug.WriteLine($"[SystemSpeech] 识别错误: {e.Error.Message}");
                            }
                        }
                        catch { }
                        finally
                        {
                            doneEvent.Set();
                        }
                    };

                    // 异步识别
                    engine.RecognizeAsync(RecognizeMode.Single);

                    // 等待完成（最多5分钟）
                    if (!doneEvent.WaitOne(TimeSpan.FromMinutes(5)))
                    {
                        try { engine.RecognizeAsyncStop(); } catch { }
                        return "[识别超时]";
                    }

                    string result = resultBuilder.ToString();
                    if (string.IsNullOrEmpty(result))
                        return "[未识别到语音内容，可能是音频质量不佳或系统语音包不支持]";

                    return result;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SystemSpeech] 识别失败: {ex.Message}");
                    return $"[系统语音识别失败: {ex.Message}]";
                }
            });
        }

        /// <summary>
        /// 检查系统是否支持中文语音识别
        /// </summary>
        public static bool IsChineseSupported()
        {
            try
            {
                foreach (var r in SpeechRecognitionEngine.InstalledRecognizers())
                {
                    if (r.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 获取已安装的识别器列表
        /// </summary>
        public static string GetInstalledRecognizersInfo()
        {
            try
            {
                var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
                if (recognizers.Count == 0)
                    return "未安装任何语音识别引擎";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"已安装 {recognizers.Count} 个识别器：");
                foreach (var r in recognizers)
                {
                    sb.AppendLine($"- {r.Culture.DisplayName} ({r.Culture.Name})");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"查询失败: {ex.Message}";
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
