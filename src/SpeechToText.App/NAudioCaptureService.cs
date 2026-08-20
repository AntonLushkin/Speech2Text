using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpeechToText.Core;

namespace SpeechToText.App
{
    public sealed class NAudioCaptureService :
        IAudioCaptureService,
        IMMNotificationClient
    {
        private static readonly TimeSpan MaximumDuration =
            TimeSpan.FromMinutes(10);

        private readonly object _sync = new object();
        private readonly MMDeviceEnumerator _enumerator =
            new MMDeviceEnumerator();
        private readonly MemoryStream _pcm16 = new MemoryStream();

        private WasapiCapture _capture;
        private MMDevice _activeDevice;
        private BufferedWaveProvider _buffer16;
        private BufferedWaveProvider _buffer24;
        private WdlResamplingSampleProvider _resampler16;
        private WdlResamplingSampleProvider _resampler24;
        private Stopwatch _stopwatch;
        private TaskCompletionSource<AudioRecording> _stopped;
        private string _activeDeviceName;
        private float _peak;
        private bool _maxDurationRaised;
        private bool _disposed;

        public NAudioCaptureService()
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
        }

        public event EventHandler<float> LevelChanged;
        public event EventHandler<byte[]> Pcm24KhzAvailable;
        public event EventHandler MaxDurationReached;
        public event EventHandler DevicesChanged;

        public IReadOnlyList<MicrophoneInfo> GetMicrophones()
        {
            var result = new List<MicrophoneInfo>
            {
                new MicrophoneInfo
                {
                    Id = string.Empty,
                    Name = "Системный микрофон (по умолчанию)",
                    IsDefault = true
                }
            };

            string defaultId = null;
            try
            {
                using (var defaultDevice = _enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Capture,
                    Role.Multimedia))
                {
                    defaultId = defaultDevice.ID;
                }
            }
            catch
            {
            }

            try
            {
                foreach (var device in _enumerator.EnumerateAudioEndPoints(
                    DataFlow.Capture,
                    DeviceState.Active))
                {
                    using (device)
                    {
                        result.Add(new MicrophoneInfo
                        {
                            Id = device.ID,
                            Name = device.FriendlyName,
                            IsDefault = string.Equals(
                                device.ID,
                                defaultId,
                                StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
            }
            catch
            {
                // The synthetic system-default entry remains usable.
            }

            return result;
        }

        public Task StartAsync(
            string microphoneId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();

            lock (_sync)
            {
                if (_capture != null)
                {
                    throw new InvalidOperationException("Запись уже идёт.");
                }

                _pcm16.SetLength(0);
                _peak = 0;
                _maxDurationRaised = false;
                _activeDevice = ResolveDevice(microphoneId);
                _activeDeviceName = _activeDevice.FriendlyName;
                _capture = new WasapiCapture(_activeDevice);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                PreparePipelines(_capture.WaveFormat);
                _stopped = new TaskCompletionSource<AudioRecording>();
                _stopwatch = Stopwatch.StartNew();
                _capture.StartRecording();
            }

            return Task.CompletedTask;
        }

        public async Task<AudioRecording> StopAsync(
            CancellationToken cancellationToken)
        {
            Task<AudioRecording> result;
            TaskCompletionSource<AudioRecording> completion;
            WasapiCapture capture;
            lock (_sync)
            {
                if (_capture == null || _stopped == null)
                {
                    throw new InvalidOperationException("Запись не запущена.");
                }

                completion = _stopped;
                result = completion.Task;
                capture = _capture;
            }

            capture.StopRecording();
            using (cancellationToken.Register(
                () => completion.TrySetCanceled(),
                useSynchronizationContext: false))
            {
                return await result.ConfigureAwait(false);
            }
        }

        public void Cancel()
        {
            WasapiCapture capture;
            lock (_sync)
            {
                if (_capture == null)
                {
                    return;
                }

                _stopped?.TrySetCanceled();
                capture = _capture;
            }
            capture.StopRecording();
        }

        private void OnDataAvailable(
            object sender,
            WaveInEventArgs eventArgs)
        {
            try
            {
                lock (_sync)
                {
                    if (_capture == null)
                    {
                        return;
                    }

                    _buffer16.AddSamples(
                        eventArgs.Buffer,
                        0,
                        eventArgs.BytesRecorded);
                    _buffer24.AddSamples(
                        eventArgs.Buffer,
                        0,
                        eventArgs.BytesRecorded);

                    Drain16Khz();
                    Drain24Khz();

                    if (!_maxDurationRaised &&
                        _stopwatch.Elapsed >= MaximumDuration)
                    {
                        _maxDurationRaised = true;
                        MaxDurationReached?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch
            {
                lock (_sync)
                {
                    _capture?.StopRecording();
                }
            }
        }

        private void Drain16Khz()
        {
            var samples = new float[4096];
            float peak = 0;
            int read;
            while ((read = _resampler16.Read(samples, 0, samples.Length)) > 0)
            {
                var pcm = ConvertToPcm16(samples, read, out var blockPeak);
                _pcm16.Write(pcm, 0, pcm.Length);
                peak = Math.Max(peak, blockPeak);
            }

            if (peak > 0)
            {
                _peak = Math.Max(_peak, peak);
            }
            LevelChanged?.Invoke(this, peak);
        }

        private void Drain24Khz()
        {
            var samples = new float[6144];
            int read;
            while ((read = _resampler24.Read(samples, 0, samples.Length)) > 0)
            {
                float ignored;
                var pcm = ConvertToPcm16(samples, read, out ignored);
                Pcm24KhzAvailable?.Invoke(this, pcm);
            }
        }

        private void OnRecordingStopped(
            object sender,
            StoppedEventArgs eventArgs)
        {
            lock (_sync)
            {
                _stopwatch?.Stop();
                var completion = _stopped;
                if (eventArgs.Exception != null)
                {
                    completion?.TrySetException(eventArgs.Exception);
                }
                else if (completion != null && !completion.Task.IsCompleted)
                {
                    FlushResamplers();
                    completion.TrySetResult(new AudioRecording
                    {
                        Wav16Khz = CreateWaveFile(_pcm16.ToArray(), 16000),
                        Duration = _stopwatch?.Elapsed ?? TimeSpan.Zero,
                        MicrophoneName = _activeDeviceName,
                        PeakLevel = _peak
                    });
                }

                CleanupCapture();
            }
        }

        private void FlushResamplers()
        {
            if (_capture == null || _buffer16 == null || _buffer24 == null)
            {
                return;
            }

            // WDL keeps a small tail while resampling. A short block of silence
            // releases that tail so very short dictations still produce a valid
            // WAV for the emergency batch fallback.
            var format = _capture.WaveFormat;
            var byteCount = Math.Max(
                format.BlockAlign,
                format.AverageBytesPerSecond / 10);
            byteCount -= byteCount % format.BlockAlign;
            var silence = new byte[byteCount];
            _buffer16.AddSamples(silence, 0, silence.Length);
            _buffer24.AddSamples(silence, 0, silence.Length);
            Drain16Khz();
            Drain24Khz();
        }

        private void PreparePipelines(WaveFormat sourceFormat)
        {
            _buffer16 = NewBuffer(sourceFormat);
            _buffer24 = NewBuffer(sourceFormat);

            _resampler16 = new WdlResamplingSampleProvider(
                new MonoSampleProvider(_buffer16.ToSampleProvider()),
                16000);
            _resampler24 = new WdlResamplingSampleProvider(
                new MonoSampleProvider(_buffer24.ToSampleProvider()),
                24000);
        }

        private static BufferedWaveProvider NewBuffer(WaveFormat format)
        {
            return new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };
        }

        private MMDevice ResolveDevice(string microphoneId)
        {
            if (!string.IsNullOrWhiteSpace(microphoneId))
            {
                try
                {
                    var selected = _enumerator.GetDevice(microphoneId);
                    if (selected.State == DeviceState.Active)
                    {
                        return selected;
                    }
                    selected.Dispose();
                }
                catch
                {
                    // Automatically fall back to the current system default.
                }
            }

            return _enumerator.GetDefaultAudioEndpoint(
                DataFlow.Capture,
                Role.Multimedia);
        }

        private void CleanupCapture()
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            _activeDevice?.Dispose();
            _activeDevice = null;
            _buffer16 = null;
            _buffer24 = null;
            _resampler16 = null;
            _resampler24 = null;
            _stopped = null;
        }

        private static byte[] ConvertToPcm16(
            float[] samples,
            int count,
            out float peak)
        {
            var result = new byte[count * 2];
            peak = 0;
            for (var index = 0; index < count; index++)
            {
                var sample = Math.Max(-1f, Math.Min(1f, samples[index]));
                peak = Math.Max(peak, Math.Abs(sample));
                var value = (short)Math.Round(sample * short.MaxValue);
                result[index * 2] = (byte)(value & 0xff);
                result[index * 2 + 1] = (byte)((value >> 8) & 0xff);
            }
            return result;
        }

        internal static byte[] CreateWaveFile(byte[] pcm, int sampleRate)
        {
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                const short channels = 1;
                const short bits = 16;
                var byteRate = sampleRate * channels * bits / 8;
                var blockAlign = (short)(channels * bits / 8);

                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + pcm.Length);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write(blockAlign);
                writer.Write(bits);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(pcm.Length);
                writer.Write(pcm);
                writer.Flush();
                return output.ToArray();
            }
        }

        private void RaiseDevicesChanged()
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            RaiseDevicesChanged();
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            RaiseDevicesChanged();
        }

        public void OnDeviceRemoved(string deviceId)
        {
            RaiseDevicesChanged();
        }

        public void OnDefaultDeviceChanged(
            DataFlow flow,
            Role role,
            string defaultDeviceId)
        {
            if (flow == DataFlow.Capture)
            {
                RaiseDevicesChanged();
            }
        }

        public void OnPropertyValueChanged(
            string pwstrDeviceId,
            PropertyKey key)
        {
            RaiseDevicesChanged();
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
            WasapiCapture capture;
            lock (_sync)
            {
                capture = _capture;
                _stopped?.TrySetCanceled();
            }

            try
            {
                capture?.StopRecording();
            }
            catch
            {
            }

            lock (_sync)
            {
                if (_capture != null)
                {
                    CleanupCapture();
                }
                _pcm16.Dispose();
            }
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _enumerator.Dispose();
        }

        private sealed class MonoSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private float[] _sourceBuffer = new float[0];

            public MonoSampleProvider(ISampleProvider source)
            {
                _source = source;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                    source.WaveFormat.SampleRate,
                    1);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                var channels = _source.WaveFormat.Channels;
                var required = count * channels;
                if (_sourceBuffer.Length < required)
                {
                    _sourceBuffer = new float[required];
                }

                var read = _source.Read(_sourceBuffer, 0, required);
                var frames = read / channels;
                for (var frame = 0; frame < frames; frame++)
                {
                    float sum = 0;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        sum += _sourceBuffer[frame * channels + channel];
                    }
                    buffer[offset + frame] = sum / channels;
                }
                return frames;
            }
        }
    }
}
