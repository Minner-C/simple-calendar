using System;
using System.IO;
using System.Diagnostics;
using NAudio.Wave;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 音频录制服务：使用 NAudio 录制麦克风输入并保存为 WAV 文件
    /// </summary>
    public class AudioRecorder : IDisposable
    {
        private WaveInEvent? _waveIn;
        private WaveFileWriter? _writer;
        private string? _currentFilePath;
        private bool _isRecording;
        private Stopwatch? _stopwatch;

        /// <summary>是否正在录音</summary>
        public bool IsRecording => _isRecording;

        /// <summary>当前录音文件路径</summary>
        public string? CurrentFilePath => _currentFilePath;

        /// <summary>录音时长（使用Stopwatch精确计时，不依赖WaveFileWriter）</summary>
        public TimeSpan RecordingDuration => _stopwatch?.Elapsed ?? TimeSpan.Zero;

        /// <summary>录音状态变化事件</summary>
        public event Action<bool, TimeSpan>? StateChanged;

        /// <summary>实时音频数据回调（byte[]是16kHz/16bit/Mono PCM数据）</summary>
        public event Action<byte[], int>? AudioDataAvailable;

        /// <summary>是否启用实时回调（用于实时转写）</summary>
        public bool EnableRealtimeCallback { get; set; } = false;

        /// <summary>
        /// 开始录音
        /// </summary>
        /// <param name="customPath">自定义文件路径，不传则自动生成</param>
        public string StartRecording(string? customPath = null)
        {
            if (_isRecording)
                throw new InvalidOperationException("已经在录音中");

            // 确保目录存在（优先使用配置的输出目录）
            string baseDir;
            try
            {
                var settings = ClockSettingsManager.LoadSettings();
                if (!string.IsNullOrWhiteSpace(settings.DocumentOutputPath) && Directory.Exists(settings.DocumentOutputPath))
                {
                    baseDir = settings.DocumentOutputPath;
                }
                else
                {
                    baseDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "SimpleCalendar");
                }
            }
            catch
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "SimpleCalendar");
            }
            string dir = Path.Combine(baseDir, "Recordings");
            Directory.CreateDirectory(dir);

            _currentFilePath = customPath ?? Path.Combine(dir,
                $"录音_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            try
            {
                // 16kHz, 16bit, Mono - 适合语音识别
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1)
                };

                _writer = new WaveFileWriter(_currentFilePath, _waveIn.WaveFormat);

                _waveIn.DataAvailable += (s, e) =>
                {
                    try
                    {
                        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                        StateChanged?.Invoke(true, _writer?.TotalTime ?? TimeSpan.Zero);
                        // 实时音频回调（用于实时转写）
                        if (EnableRealtimeCallback)
                        {
                            AudioDataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Recorder] 写入失败: {ex.Message}");
                    }
                };

                _waveIn.RecordingStopped += (s, e) =>
                {
                    try
                    {
                        _writer?.Flush();
                        _writer?.Dispose();
                        _writer = null;
                    }
                    catch { }
                    StateChanged?.Invoke(false, TimeSpan.Zero);
                };

                _waveIn.StartRecording();
                _isRecording = true;
                _stopwatch = Stopwatch.StartNew();
                Debug.WriteLine($"[Recorder] 开始录音: {_currentFilePath}");
                StateChanged?.Invoke(true, TimeSpan.Zero);
                return _currentFilePath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Recorder] 启动失败: {ex.Message}");
                Cleanup();
                throw new InvalidOperationException($"无法启动录音：{ex.Message}。请检查麦克风是否可用。");
            }
        }

        /// <summary>
        /// 停止录音并返回文件路径
        /// </summary>
        public string StopRecording()
        {
            if (!_isRecording || _waveIn == null)
                throw new InvalidOperationException("未在录音中");

            string path = _currentFilePath ?? "";
            try
            {
                _waveIn.StopRecording();
                _isRecording = false;
                _stopwatch?.Stop();
                Debug.WriteLine($"[Recorder] 停止录音: {path}, 时长: {RecordingDuration}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Recorder] 停止失败: {ex.Message}");
            }
            finally
            {
                _waveIn?.Dispose();
                _waveIn = null;
            }
            return path;
        }

        /// <summary>
        /// 取消录音（删除文件）
        /// </summary>
        public void CancelRecording()
        {
            if (_isRecording)
            {
                try
                {
                    _waveIn?.StopRecording();
                    _isRecording = false;
                    _stopwatch?.Stop();
                }
                catch { }
            }
            Cleanup();
            // 删除未完成的录音文件
            try
            {
                if (!string.IsNullOrEmpty(_currentFilePath) && File.Exists(_currentFilePath))
                    File.Delete(_currentFilePath);
            }
            catch { }
        }

        private void Cleanup()
        {
            try
            {
                _writer?.Dispose();
                _waveIn?.Dispose();
            }
            catch { }
            _writer = null;
            _waveIn = null;
            _stopwatch = null;
        }

        public void Dispose()
        {
            if (_isRecording)
                CancelRecording();
            else
                Cleanup();
        }

        /// <summary>
        /// 获取录音文件大小（KB）
        /// </summary>
        public static long GetFileSizeKB(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Length / 1024;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 获取 WAV 文件时长
        /// </summary>
        public static TimeSpan GetWavDuration(string path)
        {
            try
            {
                using var reader = new WaveFileReader(path);
                return reader.TotalTime;
            }
            catch { return TimeSpan.Zero; }
        }
    }
}
