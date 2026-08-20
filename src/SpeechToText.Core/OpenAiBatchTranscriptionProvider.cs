using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpeechToText.Core
{
    public sealed class OpenAiBatchTranscriptionProvider : ITranscriptionProvider
    {
        private static readonly Uri Endpoint =
            new Uri("https://api.openai.com/v1/audio/transcriptions");

        private readonly HttpClient _httpClient;

        public OpenAiBatchTranscriptionProvider(HttpClient httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
        }

        public async Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            string apiKey,
            CancellationToken cancellationToken)
        {
            if (request?.Recording?.Wav16Khz == null ||
                request.Recording.Wav16Khz.Length == 0)
            {
                throw new ArgumentException("В записи нет звука.", nameof(request));
            }

            if (!IsSupportedWave(request.Recording.Wav16Khz))
            {
                throw new InvalidOperationException(
                    "Резервная запись не содержит пригодного аудио. " +
                    "Попробуйте удерживать клавиши чуть дольше и проверьте микрофон.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Не задан API-ключ OpenAI.");
            }

            var stopwatch = Stopwatch.StartNew();
            Exception lastError = null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(45));
                        var text = await SendAsync(request, apiKey, timeout.Token)
                            .ConfigureAwait(false);

                        stopwatch.Stop();
                        return new TranscriptionResult
                        {
                            Text = text.Trim(),
                            Mode = RecognitionMode.Economy,
                            Elapsed = stopwatch.Elapsed,
                            AudioDuration = request.Recording.Duration,
                            EstimatedCostUsd = EstimateCost(request.Recording.Duration)
                        };
                    }
                }
                catch (Exception exception) when (
                    attempt == 0 &&
                    IsTransient(exception, cancellationToken))
                {
                    lastError = exception;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            throw lastError ?? new InvalidOperationException(
                "OpenAI не вернул результат распознавания.");
        }

        private async Task<string> SendAsync(
            TranscriptionRequest request,
            string apiKey,
            CancellationToken cancellationToken)
        {
            using (var form = new MultipartFormDataContent())
            using (var message = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                var audio = new ByteArrayContent(request.Recording.Wav16Khz);
                audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(audio, "file", "dictation.wav");
                form.Add(new StringContent("gpt-4o-mini-transcribe"), "model");
                form.Add(new StringContent(
                    string.IsNullOrWhiteSpace(request.Language) ? "ru" : request.Language),
                    "language");
                form.Add(new StringContent("text"), "response_format");

                var prompt = BuildPrompt(request.Vocabulary);
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
                }

                message.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                message.Content = form;

                using (var response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync()
                        .ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new ApiRequestException(
                            "OpenAI",
                            response.StatusCode,
                            DescribeFailure(response.StatusCode, body));
                    }

                    return body;
                }
            }
        }

        private static string BuildPrompt(IReadOnlyList<string> vocabulary)
        {
            if (vocabulary == null || vocabulary.Count == 0)
            {
                return string.Empty;
            }

            var terms = new List<string>();
            foreach (var item in vocabulary)
            {
                var term = item?.Trim();
                if (!string.IsNullOrEmpty(term))
                {
                    terms.Add(term);
                }
            }

            return terms.Count == 0
                ? string.Empty
                : "Словарь имён и терминов: " + string.Join(", ", terms);
        }

        private static bool IsTransient(
            Exception exception,
            CancellationToken outerCancellation)
        {
            if (outerCancellation.IsCancellationRequested)
            {
                return false;
            }

            if (exception is HttpRequestException ||
                exception is TaskCanceledException)
            {
                return true;
            }

            var apiError = exception as ApiRequestException;
            if (apiError == null)
            {
                return false;
            }

            var code = (int)apiError.StatusCode;
            return apiError.StatusCode == HttpStatusCode.RequestTimeout ||
                   code == 429 ||
                   code >= 500;
        }

        private static string DescribeFailure(HttpStatusCode status, string body)
        {
            var text = string.IsNullOrWhiteSpace(body)
                ? status.ToString()
                : body.Trim();

            if (text.Length > 600)
            {
                text = text.Substring(0, 600);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "OpenAI вернул ошибку {0}: {1}",
                (int)status,
                text);
        }

        private static decimal EstimateCost(TimeSpan duration)
        {
            // Approximate model price. The value is deliberately isolated here so
            // it can be updated without touching usage history or the UI.
            return Math.Round((decimal)duration.TotalMinutes * 0.003m, 6);
        }

        internal static bool IsSupportedWave(byte[] wave)
        {
            const int headerSize = 44;
            const int minimumPcmBytes = 1600;
            if (wave == null || wave.Length < headerSize + minimumPcmBytes)
            {
                return false;
            }

            return wave[0] == (byte)'R' &&
                   wave[1] == (byte)'I' &&
                   wave[2] == (byte)'F' &&
                   wave[3] == (byte)'F' &&
                   wave[8] == (byte)'W' &&
                   wave[9] == (byte)'A' &&
                   wave[10] == (byte)'V' &&
                   wave[11] == (byte)'E' &&
                   BitConverter.ToInt16(wave, 20) == 1 &&
                   BitConverter.ToInt16(wave, 22) == 1 &&
                   BitConverter.ToInt32(wave, 24) == 16000 &&
                   BitConverter.ToInt16(wave, 34) == 16 &&
                   BitConverter.ToInt32(wave, 40) >= minimumPcmBytes &&
                   BitConverter.ToInt32(wave, 40) <= wave.Length - headerSize;
        }
    }

    public sealed class ApiRequestException : Exception
    {
        public ApiRequestException(
            string provider,
            HttpStatusCode statusCode,
            string message)
            : base(message)
        {
            Provider = provider;
            StatusCode = statusCode;
        }

        public string Provider { get; }
        public HttpStatusCode StatusCode { get; }
    }
}
