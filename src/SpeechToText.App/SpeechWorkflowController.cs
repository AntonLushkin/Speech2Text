using System;
using System.Diagnostics;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using SpeechToText.Core;

namespace SpeechToText.App
{
    public sealed class SpeechWorkflowController : IDisposable
    {
        private readonly WorkflowStateMachine _state =
            new WorkflowStateMachine();
        private readonly IAudioCaptureService _audioCapture;
        private readonly ITranscriptionProvider _batchProvider;
        private readonly IRealtimeTranscriptionSessionFactory _realtimeFactory;
        private readonly ITextPostProcessor _postProcessor;
        private readonly ITextInserter _textInserter;
        private readonly ICredentialStore _credentialStore;
        private readonly IHistoryStore _historyStore;
        private readonly OverlayWindow _overlay;
        private readonly TrayController _tray;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private AppSettings _settings;
        private CancellationTokenSource _operation;
        private IRealtimeTranscriptionSession _realtime;
        private Task _realtimeStart;
        private IntPtr _targetWindow;
        private RecognitionMode _activeMode;
        private Stopwatch _totalTime;
        private AudioRecording _failedRecording;
        private IntPtr _failedTargetWindow;
        private bool _disposed;

        public SpeechWorkflowController(
            AppSettings settings,
            IAudioCaptureService audioCapture,
            ITranscriptionProvider batchProvider,
            IRealtimeTranscriptionSessionFactory realtimeFactory,
            ITextPostProcessor postProcessor,
            ITextInserter textInserter,
            ICredentialStore credentialStore,
            IHistoryStore historyStore,
            OverlayWindow overlay,
            TrayController tray)
        {
            _settings = settings;
            _audioCapture = audioCapture;
            _batchProvider = batchProvider;
            _realtimeFactory = realtimeFactory;
            _postProcessor = postProcessor;
            _textInserter = textInserter;
            _credentialStore = credentialStore;
            _historyStore = historyStore;
            _overlay = overlay;
            _tray = tray;

            _audioCapture.LevelChanged += OnLevelChanged;
            _audioCapture.Pcm24KhzAvailable += OnPcm24KhzAvailable;
            _audioCapture.MaxDurationReached += OnMaxDurationReached;
        }

        public WorkflowState State => _state.State;

        public void UpdateSettings(AppSettings settings)
        {
            _settings = settings;
        }

        public async Task BeginRecordingAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (_disposed || _state.State == WorkflowState.Recording ||
                    _state.State == WorkflowState.Transcribing ||
                    _state.State == WorkflowState.Editing ||
                    _state.State == WorkflowState.Inserting)
                {
                    return;
                }

                ResetTerminalState();
                var openAiKey = _credentialStore.Read(
                    SettingsWindow.OpenAiCredentialName);
                if (string.IsNullOrWhiteSpace(openAiKey))
                {
                    ShowError(
                        "Добавьте API-ключ OpenAI в настройках.",
                        openSettingsHint: true);
                    return;
                }

                ClearFailedAudio();
                _operation = new CancellationTokenSource();
                _targetWindow = _textInserter.CaptureTargetWindow();
                _activeMode = _settings.Mode;
                _totalTime = Stopwatch.StartNew();
                _state.Transition(WorkflowState.Recording);

                if (_settings.ShowOverlay)
                {
                    _overlay.ShowRecording(_activeMode);
                }
                if (_settings.EnableSounds)
                {
                    SystemSounds.Asterisk.Play();
                }

                await _audioCapture.StartAsync(
                    _settings.MicrophoneId,
                    _operation.Token).ConfigureAwait(true);

                if (_activeMode == RecognitionMode.Fast)
                {
                    _realtime = _realtimeFactory.Create(
                        openAiKey,
                        _settings.Language);
                    _realtime.PartialTranscript += OnPartialTranscript;
                    _realtimeStart = _realtime.StartAsync(_operation.Token);
                }
            }
            catch (Exception exception)
            {
                TryTransitionToError();
                ShowError(UserMessage(exception));
                CleanupOperation();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task FinishRecordingAsync()
        {
            AudioRecording recording = null;
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (_disposed || _state.State != WorkflowState.Recording)
                {
                    return;
                }

                _state.Transition(WorkflowState.Transcribing);
                if (_settings.ShowOverlay)
                {
                    _overlay.ShowState(
                        WorkflowState.Transcribing,
                        ModeDescription(_activeMode));
                }

                recording = await _audioCapture.StopAsync(
                    _operation.Token).ConfigureAwait(true);
                var openAiKey = _credentialStore.Read(
                    SettingsWindow.OpenAiCredentialName);

                TranscriptionResult transcript = null;
                if (_activeMode == RecognitionMode.Fast && _realtime != null)
                {
                    try
                    {
                        if (_realtimeStart != null)
                        {
                            await _realtimeStart.ConfigureAwait(true);
                        }
                        transcript = await _realtime.CompleteAsync(
                            recording.Duration,
                            _operation.Token).ConfigureAwait(true);
                    }
                    catch (Exception exception) when (
                        !(exception is OperationCanceledException))
                    {
                        if (_settings.ShowOverlay)
                        {
                            _overlay.ShowState(
                                WorkflowState.Transcribing,
                                "Быстрый режим недоступен — распознаю экономично");
                        }
                    }
                }

                if (transcript == null)
                {
                    transcript = await _batchProvider.TranscribeAsync(
                        new TranscriptionRequest
                        {
                            Recording = recording,
                            Language = _settings.Language,
                            Vocabulary = _settings.Vocabulary
                        },
                        openAiKey,
                        _operation.Token).ConfigureAwait(true);
                }

                var rawText = TextCommandFormatter.Apply(transcript.Text);
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    throw new InvalidOperationException(
                        "Речь не распознана. Попробуйте говорить ближе к микрофону.");
                }

                var finalText = rawText;
                if (_settings.EnableDeepSeek)
                {
                    var deepSeekKey = _credentialStore.Read(
                        SettingsWindow.DeepSeekCredentialName);
                    if (!string.IsNullOrWhiteSpace(deepSeekKey))
                    {
                        _state.Transition(WorkflowState.Editing);
                        if (_settings.ShowOverlay)
                        {
                            _overlay.ShowState(
                                WorkflowState.Editing,
                                "Пунктуация, абзацы и явные повторы");
                        }

                        try
                        {
                            var edited = await _postProcessor.ProcessAsync(
                                new TextProcessingRequest
                                {
                                    Text = rawText,
                                    Vocabulary = _settings.Vocabulary
                                },
                                deepSeekKey,
                                _operation.Token).ConfigureAwait(true);
                            if (!string.IsNullOrWhiteSpace(edited.Text))
                            {
                                finalText = TextCommandFormatter.Apply(
                                    edited.Text);
                                transcript.EstimatedCostUsd +=
                                    edited.EstimatedCostUsd;
                            }
                        }
                        catch (Exception exception) when (
                            !(exception is OperationCanceledException))
                        {
                            finalText = rawText;
                        }
                    }
                }

                _state.Transition(WorkflowState.Inserting);
                if (_settings.ShowOverlay)
                {
                    _overlay.ShowState(
                        WorkflowState.Inserting,
                        "Возвращаю текст в исходное окно");
                }

                var insert = await _textInserter.InsertAsync(
                    _targetWindow,
                    finalText,
                    _operation.Token).ConfigureAwait(true);

                _totalTime.Stop();
                _historyStore.Append(new HistoryEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    RawText = rawText,
                    CorrectedText = finalText,
                    Mode = transcript.Mode,
                    MicrophoneName = recording.MicrophoneName,
                    AudioSeconds = recording.Duration.TotalSeconds,
                    TotalLatencyMilliseconds = _totalTime.Elapsed.TotalMilliseconds,
                    EstimatedCostUsd = transcript.EstimatedCostUsd,
                    Status = insert.Inserted ? "Вставлено" : "В буфере"
                });

                _state.Transition(WorkflowState.Completed);
                ClearFailedAudio();
                if (_settings.ShowOverlay)
                {
                    _overlay.ShowState(
                        WorkflowState.Completed,
                        insert.Message,
                        1800);
                }
                if (_settings.EnableSounds)
                {
                    SystemSounds.Exclamation.Play();
                }
            }
            catch (OperationCanceledException)
            {
                TryTransitionToCancelled();
                if (_settings.ShowOverlay)
                {
                    _overlay.ShowState(
                        WorkflowState.Cancelled,
                        "Результат не сохранён",
                        1200);
                }
            }
            catch (Exception exception)
            {
                TryTransitionToError();
                if (recording != null)
                {
                    _failedRecording = recording;
                    _failedTargetWindow = _targetWindow;
                    _tray.SetRecoveryAvailable(true);
                    ShowError(
                        UserMessage(exception) +
                        " Аудио осталось только в памяти — повторите из трея.");
                }
                else
                {
                    ShowError(UserMessage(exception));
                }
            }
            finally
            {
                CleanupOperation();
                _gate.Release();
            }
        }

        public async Task RetryFailedAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (_disposed || _failedRecording == null ||
                    _state.State == WorkflowState.Recording ||
                    _state.State == WorkflowState.Transcribing ||
                    _state.State == WorkflowState.Editing ||
                    _state.State == WorkflowState.Inserting)
                {
                    return;
                }

                ResetTerminalState();
                var recording = _failedRecording;
                _tray.SetRecoveryAvailable(false);
                _operation = new CancellationTokenSource();
                _targetWindow = _failedTargetWindow;
                _totalTime = Stopwatch.StartNew();
                _state.Transition(WorkflowState.Transcribing);
                if (_settings.ShowOverlay)
                {
                    _overlay.ShowState(
                        WorkflowState.Transcribing,
                        "Повторная отправка сохранённого аудио");
                }

                var openAiKey = _credentialStore.Read(
                    SettingsWindow.OpenAiCredentialName);
                if (string.IsNullOrWhiteSpace(openAiKey))
                {
                    throw new InvalidOperationException(
                        "Добавьте API-ключ OpenAI в настройках.");
                }

                var transcript = await _batchProvider.TranscribeAsync(
                    new TranscriptionRequest
                    {
                        Recording = recording,
                        Language = _settings.Language,
                        Vocabulary = _settings.Vocabulary
                    },
                    openAiKey,
                    _operation.Token).ConfigureAwait(true);

                var rawText = TextCommandFormatter.Apply(transcript.Text);
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    throw new InvalidOperationException(
                        "Речь не распознана. Проверьте выбранный микрофон.");
                }

                var finalText = rawText;
                if (_settings.EnableDeepSeek)
                {
                    var deepSeekKey = _credentialStore.Read(
                        SettingsWindow.DeepSeekCredentialName);
                    if (!string.IsNullOrWhiteSpace(deepSeekKey))
                    {
                        _state.Transition(WorkflowState.Editing);
                        if (_settings.ShowOverlay)
                        {
                            _overlay.ShowState(
                                WorkflowState.Editing,
                                "Пунктуация, абзацы и явные повторы");
                        }

                        try
                        {
                            var edited = await _postProcessor.ProcessAsync(
                                new TextProcessingRequest
                                {
                                    Text = rawText,
                                    Vocabulary = _settings.Vocabulary
                                },
                                deepSeekKey,
                                _operation.Token).ConfigureAwait(true);
                            if (!string.IsNullOrWhiteSpace(edited.Text))
                            {
                                finalText = TextCommandFormatter.Apply(
                                    edited.Text);
                                transcript.EstimatedCostUsd +=
                                    edited.EstimatedCostUsd;
                            }
                        }
                        catch (Exception exception) when (
                            !(exception is OperationCanceledException))
                        {
                            finalText = rawText;
                        }
                    }
                }

                _state.Transition(WorkflowState.Inserting);
                if (_settings.ShowOverlay)
                {
                    _overlay.ShowState(
                        WorkflowState.Inserting,
                        "Возвращаю текст в исходное окно");
                }

                var insert = await _textInserter.InsertAsync(
                    _targetWindow,
                    finalText,
                    _operation.Token).ConfigureAwait(true);
                _totalTime.Stop();
                _historyStore.Append(new HistoryEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    RawText = rawText,
                    CorrectedText = finalText,
                    Mode = RecognitionMode.Economy,
                    MicrophoneName = recording.MicrophoneName,
                    AudioSeconds = recording.Duration.TotalSeconds,
                    TotalLatencyMilliseconds = _totalTime.Elapsed.TotalMilliseconds,
                    EstimatedCostUsd = transcript.EstimatedCostUsd,
                    Status = insert.Inserted ? "Вставлено" : "В буфере"
                });

                _state.Transition(WorkflowState.Completed);
                ClearFailedAudio();
                if (_settings.ShowOverlay)
                {
                    _overlay.ShowState(
                        WorkflowState.Completed,
                        insert.Message,
                        1800);
                }
            }
            catch (OperationCanceledException)
            {
                TryTransitionToCancelled();
                ClearFailedAudio();
                if (_settings.ShowOverlay)
                {
                    _overlay.ShowState(
                        WorkflowState.Cancelled,
                        "Повтор отменён",
                        1200);
                }
            }
            catch (Exception exception)
            {
                TryTransitionToError();
                _tray.SetRecoveryAvailable(true);
                ShowError(
                    UserMessage(exception) +
                    " Аудио всё ещё хранится только в памяти.");
            }
            finally
            {
                CleanupOperation();
                _gate.Release();
            }
        }

        public void DiscardFailedAudio()
        {
            ClearFailedAudio();
            if (_settings.ShowOverlay)
            {
                _overlay.ShowState(
                    WorkflowState.Cancelled,
                    "Неудачная запись удалена из памяти",
                    1200);
            }
        }

        public async Task CancelAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (_state.State != WorkflowState.Recording)
                {
                    return;
                }

                _operation?.Cancel();
                _audioCapture.Cancel();
                if (_realtime != null)
                {
                    await _realtime.CancelAsync().ConfigureAwait(true);
                }
                _state.Transition(WorkflowState.Cancelled);
                if (_settings.ShowOverlay)
                {
                    _overlay.ShowState(
                        WorkflowState.Cancelled,
                        "Диктовка отменена",
                        1200);
                }
                CleanupOperation();
            }
            finally
            {
                _gate.Release();
            }
        }

        private void OnLevelChanged(object sender, float level)
        {
            if (_settings.ShowOverlay && _state.State == WorkflowState.Recording)
            {
                _overlay.UpdateLevel(level);
            }
        }

        private void OnPcm24KhzAvailable(object sender, byte[] pcm)
        {
            if (_activeMode == RecognitionMode.Fast &&
                _state.State == WorkflowState.Recording)
            {
                _realtime?.QueueAudio(pcm);
            }
        }

        private void OnPartialTranscript(object sender, string text)
        {
            if (_settings.ShowOverlay && _settings.ShowPartialText)
            {
                _overlay.UpdatePartial(text);
            }
        }

        private void OnMaxDurationReached(object sender, EventArgs e)
        {
            _ = FinishRecordingAsync();
        }

        private void ResetTerminalState()
        {
            if (_state.State == WorkflowState.Completed ||
                _state.State == WorkflowState.Error ||
                _state.State == WorkflowState.Cancelled)
            {
                _state.Transition(WorkflowState.Idle);
            }
        }

        private void TryTransitionToError()
        {
            if (_state.State == WorkflowState.Recording ||
                _state.State == WorkflowState.Transcribing ||
                _state.State == WorkflowState.Editing ||
                _state.State == WorkflowState.Inserting)
            {
                _state.TryTransition(WorkflowState.Error);
            }
        }

        private void TryTransitionToCancelled()
        {
            if (_state.State == WorkflowState.Recording ||
                _state.State == WorkflowState.Transcribing ||
                _state.State == WorkflowState.Editing)
            {
                _state.TryTransition(WorkflowState.Cancelled);
            }
        }

        private void ShowError(string message, bool openSettingsHint = false)
        {
            if (_settings.ShowOverlay)
            {
                _overlay.ShowState(
                    WorkflowState.Error,
                    message,
                    3000);
            }
            _tray.ShowBalloon(
                "Диктовка",
                openSettingsHint
                    ? message + " Откройте настройки двойным щелчком по значку."
                    : message,
                error: true);
        }

        private void CleanupOperation()
        {
            if (_realtime != null)
            {
                _realtime.PartialTranscript -= OnPartialTranscript;
                _realtime.Dispose();
            }
            _realtime = null;
            _realtimeStart = null;
            _operation?.Dispose();
            _operation = null;
            _targetWindow = IntPtr.Zero;
        }

        private void ClearFailedAudio()
        {
            _failedRecording = null;
            _failedTargetWindow = IntPtr.Zero;
            _tray.SetRecoveryAvailable(false);
        }

        private static string ModeDescription(RecognitionMode mode)
        {
            return mode == RecognitionMode.Fast
                ? "⚡ Быстрый режим"
                : "₽ Экономичный режим";
        }

        private static string UserMessage(Exception exception)
        {
            if (exception is TaskCanceledException)
            {
                return "Превышено время ожидания ответа.";
            }

            return string.IsNullOrWhiteSpace(exception.Message)
                ? "Не удалось обработать диктовку."
                : exception.Message;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _audioCapture.LevelChanged -= OnLevelChanged;
            _audioCapture.Pcm24KhzAvailable -= OnPcm24KhzAvailable;
            _audioCapture.MaxDurationReached -= OnMaxDurationReached;
            _operation?.Cancel();
            _audioCapture.Cancel();
            CleanupOperation();
            ClearFailedAudio();
            _gate.Dispose();
        }
    }
}
