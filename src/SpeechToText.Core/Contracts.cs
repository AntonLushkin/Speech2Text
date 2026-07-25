using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SpeechToText.Core
{
    public interface ITranscriptionProvider
    {
        Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            string apiKey,
            CancellationToken cancellationToken);
    }

    public interface IRealtimeTranscriptionSession : IDisposable
    {
        event EventHandler<string> PartialTranscript;
        Task StartAsync(CancellationToken cancellationToken);
        void QueueAudio(byte[] pcm24Khz);
        Task<TranscriptionResult> CompleteAsync(
            TimeSpan audioDuration,
            CancellationToken cancellationToken);
        Task CancelAsync();
    }

    public interface IRealtimeTranscriptionSessionFactory
    {
        IRealtimeTranscriptionSession Create(string apiKey, string language);
    }

    public interface ITextPostProcessor
    {
        Task<TextProcessingResult> ProcessAsync(
            TextProcessingRequest request,
            string apiKey,
            CancellationToken cancellationToken);
    }

    public interface IAudioCaptureService : IDisposable
    {
        event EventHandler<float> LevelChanged;
        event EventHandler<byte[]> Pcm24KhzAvailable;
        event EventHandler MaxDurationReached;
        event EventHandler DevicesChanged;
        IReadOnlyList<MicrophoneInfo> GetMicrophones();
        Task StartAsync(string microphoneId, CancellationToken cancellationToken);
        Task<AudioRecording> StopAsync(CancellationToken cancellationToken);
        void Cancel();
    }

    public interface ITextInserter
    {
        IntPtr CaptureTargetWindow();
        Task<InsertResult> InsertAsync(
            IntPtr targetWindow,
            string text,
            CancellationToken cancellationToken);
    }

    public interface IHistoryStore
    {
        IReadOnlyList<HistoryEntry> Load();
        void Append(HistoryEntry entry);
        void Clear();
        UsageSummary Summarize();
    }

    public interface ICredentialStore
    {
        string Read(string name);
        void Write(string name, string secret);
        void Delete(string name);
    }

    public interface IAutoStartService
    {
        AutoStartStatus GetStatus();
        bool IsEnabled { get; }
        void SetEnabled(bool enabled);
        void RepairIfNeeded();
    }
}
