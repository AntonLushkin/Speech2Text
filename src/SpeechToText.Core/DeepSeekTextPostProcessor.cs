using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SpeechToText.Core
{
    public sealed class DeepSeekTextPostProcessor : ITextPostProcessor
    {
        private static readonly Uri Endpoint =
            new Uri("https://api.deepseek.com/chat/completions");

        private const string SystemPrompt =
            "Ты аккуратный редактор русской диктовки с английскими терминами. " +
            "Исправь только пунктуацию, регистр и деление на абзацы. " +
            "Удаляй слова-паразиты и только явные случайные повторы. " +
            "Сохраняй смысл, стиль, имена, числа, ссылки и английские термины. " +
            "Команды «новый абзац», «новая строка», «открой кавычки» и " +
            "«закрой кавычки» преобразуй в соответствующее форматирование. " +
            "Не добавляй фактов и не отвечай на содержание. " +
            "Верни только готовый текст без пояснений и обрамляющих кавычек.";

        private readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _json =
            new JavaScriptSerializer();

        public DeepSeekTextPostProcessor(HttpClient httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
        }

        public async Task<TextProcessingResult> ProcessAsync(
            TextProcessingRequest request,
            string apiKey,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request?.Text))
            {
                return new TextProcessingResult
                {
                    Text = string.Empty
                };
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Не задан API-ключ DeepSeek.");
            }

            var stopwatch = Stopwatch.StartNew();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken))
            using (var message = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                message.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey.Trim());

                var payload = new Dictionary<string, object>
                {
                    ["model"] = "deepseek-v4-flash",
                    ["messages"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "system",
                            ["content"] = SystemPrompt
                        },
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = BuildUserText(request)
                        }
                    },
                    ["temperature"] = 0.1,
                    ["stream"] = false,
                    ["thinking"] = new Dictionary<string, object>
                    {
                        ["type"] = "disabled"
                    }
                };

                message.Content = new StringContent(
                    _json.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                using (var response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseContentRead,
                    timeout.Token).ConfigureAwait(false))
                {
                    var responseText = await response.Content.ReadAsStringAsync()
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new ApiRequestException(
                            "DeepSeek",
                            response.StatusCode,
                            "DeepSeek вернул ошибку " +
                            ((int)response.StatusCode).ToString(
                                CultureInfo.InvariantCulture) +
                            ".");
                    }

                    var document =
                        _json.Deserialize<Dictionary<string, object>>(responseText);
                    var content = ExtractContent(document);
                    var usage = ExtractUsage(document);

                    stopwatch.Stop();
                    return new TextProcessingResult
                    {
                        Text = content.Trim(),
                        InputTokens = usage.Item1,
                        OutputTokens = usage.Item2,
                        Elapsed = stopwatch.Elapsed,
                        EstimatedCostUsd = EstimateCost(
                            usage.Item1,
                            usage.Item2)
                    };
                }
            }
        }

        private static string BuildUserText(TextProcessingRequest request)
        {
            var builder = new StringBuilder();
            if (request.Vocabulary != null && request.Vocabulary.Count > 0)
            {
                builder.AppendLine(
                    "Словарь, написание которого надо сохранить:");
                builder.AppendLine(string.Join(", ", request.Vocabulary));
                builder.AppendLine();
            }

            builder.Append(request.Text);
            return builder.ToString();
        }

        private static string ExtractContent(Dictionary<string, object> document)
        {
            object choicesValue;
            var choices = document != null &&
                          document.TryGetValue("choices", out choicesValue)
                ? (choicesValue as IEnumerable)?.Cast<object>().ToList()
                : null;

            if (choices == null || choices.Count == 0)
            {
                throw new InvalidOperationException(
                    "DeepSeek вернул ответ без текста.");
            }

            var choice = choices[0] as Dictionary<string, object>;
            object messageValue;
            var message = choice != null &&
                          choice.TryGetValue("message", out messageValue)
                ? messageValue as Dictionary<string, object>
                : null;
            object contentValue;
            var content = message != null &&
                          message.TryGetValue("content", out contentValue)
                ? contentValue as string
                : null;

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException(
                    "DeepSeek вернул пустой текст.");
            }

            return content;
        }

        private static Tuple<int, int> ExtractUsage(
            Dictionary<string, object> document)
        {
            object usageValue;
            var usage = document != null &&
                        document.TryGetValue("usage", out usageValue)
                ? usageValue as Dictionary<string, object>
                : null;

            if (usage == null)
            {
                return Tuple.Create(0, 0);
            }

            object promptValue;
            object completionValue;
            return Tuple.Create(
                ToInt(usage.TryGetValue("prompt_tokens", out promptValue)
                    ? promptValue
                    : null),
                ToInt(usage.TryGetValue("completion_tokens", out completionValue)
                    ? completionValue
                    : null));
        }

        private static int ToInt(object value)
        {
            return value == null
                ? 0
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static decimal EstimateCost(
            int inputTokens,
            int outputTokens)
        {
            // Conservative estimate: cache-miss input price plus output price.
            return Math.Round(
                inputTokens * 0.14m / 1_000_000m +
                outputTokens * 0.28m / 1_000_000m,
                8);
        }
    }
}
