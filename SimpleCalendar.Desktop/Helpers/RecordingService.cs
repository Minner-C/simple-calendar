using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 录音服务：解耦录音/转写逻辑与UI
    /// 通过事件通知UI状态变化，参考WorkAny的Service层设计
    /// </summary>
    public class RecordingService
    {
        private AudioRecorder? _recorder;
        private RealtimeTranscriber? _transcriber;
        private DispatcherTimer? _timer;
        private readonly StringBuilder _liveText = new();
        private DateTime _startTime;

        public bool IsRecording { get; private set; }
        public TimeSpan Duration => IsRecording ? DateTime.Now - _startTime : TimeSpan.Zero;

        /// <summary>录音时长更新（每秒触发）</summary>
        public event Action<TimeSpan>? OnDurationUpdate;

        /// <summary>实时转写文本更新</summary>
        public event Action<string>? OnTranscriptionUpdate;

        /// <summary>录音完成（返回文件路径+转写文本+时长）</summary>
        public event Action<string, string, TimeSpan>? OnRecordingComplete;

        /// <summary>录音出错</summary>
        public event Action<string>? OnError;

        /// <summary>开始录音</summary>
        public void Start()
        {
            if (IsRecording) return;

            try
            {
                _recorder = new AudioRecorder();
                RecorderHolder.Current = _recorder;
                _liveText.Clear();
                _startTime = DateTime.Now;

                // 创建实时转写器
                try
                {
                    _transcriber = new RealtimeTranscriber();
                    _transcriber.OnSegmentRecognized += (text) =>
                    {
                        if (string.IsNullOrEmpty(text)) return;
                        lock (_liveText)
                        {
                            if (_liveText.Length > 0)
                                _liveText.Append(" ");
                            _liveText.Append(text);
                        }
                        OnTranscriptionUpdate?.Invoke(text);
                    };

                    _recorder.AudioDataAvailable += (data, length) =>
                    {
                        try { _transcriber?.WriteAudioData(data, length); } catch { }
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RecordingService] 转写器初始化失败（不影响录音）: {ex.Message}");
                }

                _recorder.StartRecording();
                IsRecording = true;

                // 启动计时器
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += (s, e) => OnDurationUpdate?.Invoke(Duration);
                _timer.Start();
            }
            catch (Exception ex)
            {
                IsRecording = false;
                OnError?.Invoke($"录音启动失败: {ex.Message}");
            }
        }

        /// <summary>停止录音</summary>
        public void Stop()
        {
            if (!IsRecording) return;
            IsRecording = false;

            string path = "";
            string transcription = "";
            TimeSpan duration = Duration;

            try
            {
                _timer?.Stop();
                _timer = null;

                path = _recorder?.StopRecording() ?? "";
                if (!string.IsNullOrEmpty(path))
                    RecorderHolder.LastRecordingPath = path;

                // 获取转写文本
                try
                {
                    var fullText = _transcriber?.Stop() ?? "";
                    if (string.IsNullOrEmpty(fullText))
                        lock (_liveText) fullText = _liveText.ToString();
                    transcription = fullText ?? "";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RecordingService] 停止转写失败: {ex.Message}");
                }

                OnRecordingComplete?.Invoke(path, transcription, duration);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"停止录音失败: {ex.Message}");
            }
            finally
            {
                _recorder = null;
                _transcriber = null;
            }
        }

        /// <summary>获取当前实时转写文本</summary>
        public string GetLiveTranscription()
        {
            lock (_liveText) return _liveText.ToString();
        }
    }
}
