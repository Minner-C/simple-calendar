using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 实时语音转写（基于 Windows 系统语音识别 System.Speech）
    /// 边录音边识别，实时输出文字
    /// </summary>
    public class RealtimeTranscriber : IDisposable
    {
        private SpeechRecognitionEngine? _engine;
        private BlockingAudioStream? _audioStream;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private readonly System.Text.StringBuilder _fullText = new();

        /// <summary>实时识别到文字事件（每段话识别完成后触发）</summary>
        public event Action<string>? OnSegmentRecognized;

        /// <summary>完整转写文本（累积）</summary>
        public string FullText => _fullText.ToString();

        /// <summary>是否正在转写</summary>
        public bool IsTranscribing { get; private set; }

        /// <summary>
        /// 开始实时转写
        /// </summary>
        public bool Start()
        {
            if (IsTranscribing) return false;

            try
            {
                var recognizerInfo = FindRecognizer();
                if (recognizerInfo == null)
                {
                    Debug.WriteLine("[RealtimeTranscriber] 未找到可用的语音识别引擎");
                    return false;
                }

                _engine = new SpeechRecognitionEngine(recognizerInfo);
                var dictationGrammar = new DictationGrammar();
                _engine.LoadGrammar(dictationGrammar);

                _audioStream = new BlockingAudioStream();
                var format = new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
                _engine.SetInputToAudioStream(_audioStream, format);

                _engine.SpeechRecognized += (s, e) =>
                {
                    try
                    {
                        if (e.Result != null && !string.IsNullOrEmpty(e.Result.Text))
                        {
                            string text = e.Result.Text;
                            _fullText.Append(text);
                            _fullText.Append(" ");
                            OnSegmentRecognized?.Invoke(text);
                            Debug.WriteLine($"[RealtimeTranscriber] 识别: {text}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[RealtimeTranscriber] 识别事件异常: {ex.Message}");
                    }
                };

                _engine.RecognizeCompleted += (s, e) =>
                {
                    Debug.WriteLine($"[RealtimeTranscriber] 识别完成, Error={e.Error?.Message}");
                };

                _cts = new CancellationTokenSource();

                _engine.RecognizeAsync(RecognizeMode.Multiple);
                IsTranscribing = true;
                Debug.WriteLine("[RealtimeTranscriber] 实时转写已启动");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RealtimeTranscriber] 启动失败: {ex.Message}");
                Cleanup();
                return false;
            }
        }

        /// <summary>
        /// 写入音频数据（由 AudioRecorder 在 DataAvailable 回调中调用）
        /// </summary>
        public void WriteAudioData(byte[] buffer, int bytesRecorded)
        {
            if (!IsTranscribing || _audioStream == null) return;
            try
            {
                _audioStream.Write(buffer, 0, bytesRecorded);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RealtimeTranscriber] 写入音频数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止实时转写，返回完整识别文本
        /// </summary>
        public string Stop()
        {
            if (!IsTranscribing) return _fullText.ToString();

            try
            {
                _cts?.Cancel();

                // 标记音频流结束，让引擎读完剩余数据
                _audioStream?.MarkEndOfStream();

                try { _engine?.RecognizeAsyncCancel(); } catch { }

                // 给引擎一点时间处理剩余音频
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RealtimeTranscriber] 停止异常: {ex.Message}");
            }

            Cleanup();
            IsTranscribing = false;
            return _fullText.ToString();
        }

        private RecognizerInfo? FindRecognizer()
        {
            try
            {
                foreach (var r in SpeechRecognitionEngine.InstalledRecognizers())
                {
                    if (r.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                        return r;
                }
                foreach (var r in SpeechRecognitionEngine.InstalledRecognizers())
                {
                    if (r.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                        return r;
                }
            }
            catch { }
            return null;
        }

        private void Cleanup()
        {
            try { _engine?.Dispose(); } catch { }
            try { _audioStream?.Dispose(); } catch { }
            _engine = null;
            _audioStream = null;
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (IsTranscribing) Stop();
                else Cleanup();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// 阻塞式音频流：Write写入数据，Read阻塞等待数据
    /// 用于将NAudio实时采集的音频数据喂给SpeechRecognitionEngine
    /// </summary>
    internal class BlockingAudioStream : Stream
    {
        private readonly ConcurrentQueue<byte[]> _queue = new();
        private byte[]? _currentBuffer;
        private int _currentOffset;
        private bool _endOfStream;
        private readonly ManualResetEventSlim _dataAvailable = new(false);
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        /// <summary>标记流结束（录音停止后调用，让识别引擎读完剩余数据）</summary>
        public void MarkEndOfStream()
        {
            _endOfStream = true;
            _dataAvailable.Set();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_endOfStream) return;
            var data = new byte[count];
            Buffer.BlockCopy(buffer, offset, data, 0, count);
            _queue.Enqueue(data);
            _dataAvailable.Set();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;

            while (totalRead < count)
            {
                if (_currentBuffer != null && _currentOffset < _currentBuffer.Length)
                {
                    int bytesToCopy = Math.Min(count - totalRead, _currentBuffer.Length - _currentOffset);
                    Buffer.BlockCopy(_currentBuffer, _currentOffset, buffer, offset + totalRead, bytesToCopy);
                    _currentOffset += bytesToCopy;
                    totalRead += bytesToCopy;
                    _position += bytesToCopy;

                    if (_currentOffset >= _currentBuffer.Length)
                    {
                        _currentBuffer = null;
                        _currentOffset = 0;
                    }
                    continue;
                }

                if (_queue.TryDequeue(out _currentBuffer))
                {
                    _currentOffset = 0;
                    _dataAvailable.Reset();
                    continue;
                }

                if (_endOfStream && _queue.IsEmpty)
                    break;

                _dataAvailable.Wait(100);
            }

            return totalRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _endOfStream = true;
                _dataAvailable.Set();
                _dataAvailable.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
