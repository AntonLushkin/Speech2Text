using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SpeechToText.Core
{
    public sealed class OpenAiRealtimeTranscriptionSessionFactory :
        IRealtimeTranscriptionSessionFactory
    {
        public IRealtimeTranscriptionSession Create(string apiKey, string language)
        {
            return new OpenAiRealtimeTranscriptionSession(apiKey, language);
        }
    }

    public sealed class OpenAiRealtimeTranscriptionSession :
        IRealtimeTranscriptionSession
    {
        private static readonly Uri Endpoint = new Uri(
            "wss://api.openai.com/v1/realtime?model=gpt-realtime-whisper");

        private readonly string _apiKey;
        private readonly string _language;
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly BlockingCollection<byte[]> _audioQueue =
            new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        private readonly CancellationTokenSource _lifetime =
            new CancellationTokenSource();
        private readonly JavaScriptSerializer _json =
            new JavaScriptSerializer();
        private readonly StringBuilder _transcript = new StringBuilder();
        private readonly TaskCompletionSource<string> _finalTranscript =
            new TaskCompletionSource<string>();
        private readonly Stopwatch _stopwatch = new Stopwatch();

        private Task _sendLoop;
        private Task _receiveLoop;
        private bool _started;
        private bool _disposed;

        public OpenAiRealtimeTranscriptionSession(
            string apiKey,
            string language)
        {
            _apiKey = apiKey;
            _language = string.IsNullOrWhiteSpace(language) ? "ru" : language;
        }

        public event EventHandler<string> PartialTranscript;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (_started)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("Не задан API-ключ OpenAI.");
            }

            _socket.Options.SetRequestHeader(
                "Authorization",
                "Bearer " + _apiKey.Trim());

            using (var connectTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await _socket.ConnectAsync(Endpoint, connectTimeout.Token)
                    .ConfigureAwait(false);
            }

            _started = true;
            _stopwatch.Start();

            await SendJsonAsync(
                new Dictionary<string, object>
                {
                    ["type"] = "session.update",
                    ["session"] = new Dictionary<string, object>
                    {
                        ["type"] = "transcription",
                        ["audio"] = new Dictionary<string, object>
                        {
                            ["input"] = new Dictionary<string, object>
                            {
                                ["format"] = new Dictionary<string, object>
                                {
                                    ["type"] = "audio/pcm",
                                    ["rate"] = 24000
                                },
                                ["transcription"] = new Dictionary<string, object>
                                {
                                    ["model"] = "gpt-realtime-whisper",
                                    ["language"] = _language,
                                    ["delay"] = "low"
                                },
                                ["turn_detection"] = null
                            }
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);

            _sendLoop = Task.Run(() => SendAudioLoopAsync(_lifetime.Token));
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token));
        }

        public void QueueAudio(byte[] pcm24Khz)
        {
            if (_audioQueue.IsAddingCompleted ||
                pcm24Khz == null || pcm24Khz.Length == 0)
            {
                return;
            }

            var copy = new byte[pcm24Khz.Length];
            Buffer.BlockCopy(pcm24Khz, 0, copy, 0, copy.Length);
            try
            {
                _audioQueue.Add(copy);
            }
            catch (InvalidOperationException)
            {
                // Completion raced with the final audio callback.
            }
        }

        public async Task<TranscriptionResult> CompleteAsync(
            TimeSpan audioDuration,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_started)
            {
                throw new InvalidOperationException(
                    "Realtime-сессия ещё не запущена.");
            }

            _audioQueue.CompleteAdding();
            if (_sendLoop != null)
            {
                await _sendLoop.ConfigureAwait(false);
            }

            await SendJsonAsync(
                new Dictionary<string, object>
                {
                    ["type"] = "input_audio_buffer.commit"
                },
                cancellationToken).ConfigureAwait(false);

            using (var finalizeTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token))
            {
                finalizeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                var completed = await WaitWithCancellationAsync(
                    _finalTranscript.Task,
                    finalizeTimeout.Token).ConfigureAwait(false);

                _stopwatch.Stop();
                return new TranscriptionResult
                {
                    Text = completed.Trim(),
                    Mode = RecognitionMode.Fast,
                    Elapsed = _stopwatch.Elapsed,
                    AudioDuration = audioDuration,
                    EstimatedCostUsd = Math.Round(
                        (decimal)audioDuration.TotalMinutes * 0.017m,
                        6)
                };
            }
        }

        public async Task CancelAsync()
        {
            if (_disposed)
            {
                return;
            }

            _audioQueue.CompleteAdding();
            _lifetime.Cancel();

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "cancelled",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Cancellation must never mask the user's command.
            }
        }

        private async Task SendAudioLoopAsync(CancellationToken cancellationToken)
        {
            foreach (var chunk in _audioQueue.GetConsumingEnumerable(
                cancellationToken))
            {
                await SendJsonAsync(
                    new Dictionary<string, object>
                    {
                        ["type"] = "input_audio_buffer.append",
                        ["audio"] = Convert.ToBase64String(chunk)
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var message = new StringBuilder();

            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       _socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (!_finalTranscript.Task.IsCompleted)
                            {
                                _finalTranscript.TrySetException(
                                    new InvalidOperationException(
                                        "Realtime-соединение было закрыто."));
                            }
                            return;
                        }

                        message.Append(Encoding.UTF8.GetString(
                            buffer,
                            0,
                            result.Count));
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        HandleEvent(message.ToString());
                    }
                    message.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                if (!_lifetime.IsCancellationRequested)
                {
                    _finalTranscript.TrySetCanceled();
                }
            }
            catch (Exception exception)
            {
                _finalTranscript.TrySetException(exception);
            }
        }

        private void HandleEvent(string json)
        {
            Dictionary<string, object> payload;
            try
            {
                payload = _json.Deserialize<Dictionary<string, object>>(json);
            }
            catch
            {
                return;
            }

            object typeValue;
            if (!payload.TryGetValue("type", out typeValue))
            {
                return;
            }

            var type = typeValue as string;
            if (type == "conversation.item.input_audio_transcription.delta")
            {
                object deltaValue;
                if (payload.TryGetValue("delta", out deltaValue))
                {
                    var delta = deltaValue as string;
                    if (!string.IsNullOrEmpty(delta))
                    {
                        _transcript.Append(delta);
                        PartialTranscript?.Invoke(this, _transcript.ToString());
                    }
                }
                return;
            }

            if (type == "conversation.item.input_audio_transcription.completed")
            {
                object transcriptValue;
                var text = payload.TryGetValue("transcript", out transcriptValue)
                    ? transcriptValue as string
                    : null;

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = _transcript.ToString();
                }

                _finalTranscript.TrySetResult(text ?? string.Empty);
                return;
            }

            if (type == "error")
            {
                _finalTranscript.TrySetException(
                    new InvalidOperationException(ExtractError(payload)));
            }
        }

        private async Task SendJsonAsync(
            object value,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(_json.Serialize(value));
            await _socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<T> WaitWithCancellationAsync<T>(
            Task<T> task,
            CancellationToken cancellationToken)
        {
            var signal = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(
                () => signal.TrySetCanceled(),
                useSynchronizationContext: false))
            {
                var completed = await Task.WhenAny(task, signal.Task)
                    .ConfigureAwait(false);
                if (completed == task)
                {
                    return await task.ConfigureAwait(false);
                }

                await signal.Task.ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private static string ExtractError(Dictionary<string, object> payload)
        {
            object errorValue;
            var error = payload.TryGetValue("error", out errorValue)
                ? errorValue as Dictionary<string, object>
                : null;
            object messageValue;
            return error != null &&
                   error.TryGetValue("message", out messageValue)
                ? "OpenAI Realtime: " + Convert.ToString(messageValue)
                : "OpenAI Realtime вернул ошибку.";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            try
            {
                _audioQueue.CompleteAdding();
            }
            catch (InvalidOperationException)
            {
            }
            _lifetime.Dispose();
            _socket.Dispose();
            _audioQueue.Dispose();
        }
    }
}
