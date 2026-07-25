using System;
using System.Collections.Generic;

namespace SpeechToText.Core
{
    public enum RecognitionMode
    {
        Economy = 0,
        Fast = 1
    }

    public enum WorkflowState
    {
        Idle,
        Recording,
        Transcribing,
        Editing,
        Inserting,
        Completed,
        Error,
        Cancelled
    }

    public enum AutoStartStatus
    {
        Disabled,
        Enabled,
        Broken
    }

    public sealed class AppSettings
    {
        public RecognitionMode Mode { get; set; } = RecognitionMode.Economy;
        public string MicrophoneId { get; set; } = string.Empty;
        public string Language { get; set; } = "ru";
        public bool EnableDeepSeek { get; set; } = true;
        public bool ShowOverlay { get; set; } = true;
        public bool ShowPartialText { get; set; }
        public bool EnableSounds { get; set; }
        public string RecordHotkey { get; set; } = "LControl+LAlt";
        public string ModeHotkey { get; set; } = "LControl+Space";
        public List<string> Vocabulary { get; set; } = new List<string>();
    }

    public sealed class MicrophoneInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsDefault { get; set; }

        public override string ToString() => Name;
    }

    public sealed class AudioRecording
    {
        public byte[] Wav16Khz { get; set; }
        public TimeSpan Duration { get; set; }
        public string MicrophoneName { get; set; }
        public float PeakLevel { get; set; }
    }

    public sealed class TranscriptionRequest
    {
        public AudioRecording Recording { get; set; }
        public string Language { get; set; }
        public IReadOnlyList<string> Vocabulary { get; set; }
    }

    public sealed class TranscriptionResult
    {
        public string Text { get; set; }
        public RecognitionMode Mode { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan AudioDuration { get; set; }
        public decimal EstimatedCostUsd { get; set; }
    }

    public sealed class TextProcessingRequest
    {
        public string Text { get; set; }
        public IReadOnlyList<string> Vocabulary { get; set; }
    }

    public sealed class TextProcessingResult
    {
        public string Text { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public TimeSpan Elapsed { get; set; }
        public decimal EstimatedCostUsd { get; set; }
    }

    public sealed class InsertResult
    {
        public bool Inserted { get; set; }
        public bool CopiedToClipboard { get; set; }
        public string Message { get; set; }
    }

    public sealed class HistoryEntry
    {
        public DateTime TimestampUtc { get; set; }
        public string RawText { get; set; }
        public string CorrectedText { get; set; }
        public RecognitionMode Mode { get; set; }
        public string MicrophoneName { get; set; }
        public double AudioSeconds { get; set; }
        public double TotalLatencyMilliseconds { get; set; }
        public decimal EstimatedCostUsd { get; set; }
        public string Status { get; set; }
    }

    public sealed class UsageSummary
    {
        public int Count { get; set; }
        public double AudioMinutes { get; set; }
        public double AverageLatencyMilliseconds { get; set; }
        public decimal EstimatedCostUsd { get; set; }
    }
}
