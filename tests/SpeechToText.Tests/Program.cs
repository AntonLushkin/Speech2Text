using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SpeechToText.App;
using SpeechToText.Core;

namespace SpeechToText.Tests
{
    internal static class Program
    {
        private static int _passed;

        private static int Main()
        {
            try
            {
                RunAsync().GetAwaiter().GetResult();
                Console.WriteLine("OK: " + _passed + " tests passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAILED: " + exception);
                return 1;
            }
        }

        private static async Task RunAsync()
        {
            TestStateMachine();
            TestTextCommands();
            TestSettingsRoundTrip();
            TestProtectedHistory();
            TestHotkeyParsing();
            TestAutoStartCommand();
            TestWaveContainer();
            TestTrayIconsEmbedded();
            TestNativeInputLayout();
            await TestBatchTranscriptionAndRetry();
            await TestDeepSeekProcessing();
        }

        private static void TestStateMachine()
        {
            var state = new WorkflowStateMachine();
            state.Transition(WorkflowState.Recording);
            state.Transition(WorkflowState.Transcribing);
            state.Transition(WorkflowState.Editing);
            state.Transition(WorkflowState.Inserting);
            state.Transition(WorkflowState.Completed);
            state.Transition(WorkflowState.Idle);
            Assert(state.State == WorkflowState.Idle, "state returns to idle");
            Assert(!state.TryTransition(WorkflowState.Completed),
                "invalid transition rejected");

            state.Transition(WorkflowState.Transcribing);
            Assert(state.State == WorkflowState.Transcribing,
                "failed audio can be retried from idle");
        }

        private static void TestTextCommands()
        {
            var input =
                "Первый блок. новый абзац Второй блок, новая строка третья. " +
                "открой кавычки пример закрой кавычки";
            var result = TextCommandFormatter.Apply(input);
            Assert(result.Contains("\n\n"), "new paragraph command");
            Assert(result.Contains("\nтретья"), "new line command");
            Assert(result.Contains("«пример»"), "quote commands");
        }

        private static void TestSettingsRoundTrip()
        {
            var root = NewTempDirectory();
            try
            {
                var store = new AppSettingsStore(root);
                store.Save(new AppSettings
                {
                    Mode = RecognitionMode.Fast,
                    MicrophoneId = "stable-device-id",
                    EnableDeepSeek = false,
                    RecordHotkey = "RAlt+RShift",
                    Vocabulary = new List<string>
                    {
                        "OpenAI",
                        "Тестовый термин"
                    }
                });
                var loaded = store.Load();
                Assert(loaded.Mode == RecognitionMode.Fast, "settings mode");
                Assert(loaded.MicrophoneId == "stable-device-id",
                    "settings microphone");
                Assert(loaded.Vocabulary.SequenceEqual(
                    new[] { "OpenAI", "Тестовый термин" }),
                    "settings vocabulary");
                Assert(loaded.RecordHotkey == "LAlt+LShift",
                    "legacy right-side hotkey migrated to left");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void TestProtectedHistory()
        {
            var root = NewTempDirectory();
            try
            {
                var store = new ProtectedHistoryStore(root);
                for (var index = 0; index < 55; index++)
                {
                    store.Append(new HistoryEntry
                    {
                        TimestampUtc = DateTime.UtcNow,
                        RawText = "секретный открытый текст " + index,
                        CorrectedText = "готовый текст " + index,
                        AudioSeconds = 12,
                        TotalLatencyMilliseconds = 900,
                        Status = "Вставлено"
                    });
                }

                Assert(store.Load().Count == 50, "history capped at 50");
                var bytes = File.ReadAllBytes(Path.Combine(root, "history.bin"));
                var visible = Encoding.UTF8.GetString(bytes);
                Assert(!visible.Contains("секретный открытый текст"),
                    "history encrypted");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void TestHotkeyParsing()
        {
            var record = GlobalKeyboardHook.ParseHotkey(
                "LControl+LAlt",
                "LControl+LAlt");
            Assert(record.SetEquals(new[] { 0xA2, 0xA4 }),
                "left-side hotkey parsed");

            var fallback = GlobalKeyboardHook.ParseHotkey(
                "unknown",
                "LControl+Space");
            Assert(fallback.SetEquals(new[] { 0xA2, 0x20 }),
                "invalid hotkey falls back");
        }

        private static void TestAutoStartCommand()
        {
            var path = @"C:\Apps With Spaces\SpeechToText.exe";
            var command = AutoStartService.BuildCommand(path);
            Assert(command ==
                   "\"C:\\Apps With Spaces\\SpeechToText.exe\" --background",
                "autostart command quoting");
            Assert(AutoStartService.TryParseExecutablePath(
                       command,
                       out var parsed) &&
                   parsed == path,
                "autostart command parsing");
            Assert(!AutoStartService.TryParseExecutablePath(
                    @"C:\unsafe.exe --background",
                    out parsed),
                "unquoted autostart rejected");
        }

        private static void TestWaveContainer()
        {
            var wave = NAudioCaptureService.CreateWaveFile(
                new byte[] { 1, 2, 3, 4 },
                16000);
            Assert(Encoding.ASCII.GetString(wave, 0, 4) == "RIFF",
                "wave RIFF header");
            Assert(Encoding.ASCII.GetString(wave, 8, 4) == "WAVE",
                "wave format header");
            Assert(BitConverter.ToInt32(wave, 24) == 16000,
                "wave sample rate");
            Assert(wave.Length == 48, "wave payload size");
        }

        private static void TestTrayIconsEmbedded()
        {
            var resources = typeof(TrayController).Assembly
                .GetManifestResourceNames();
            Assert(resources.Any(name => name.EndsWith(
                    "tray-economy.ico",
                    StringComparison.OrdinalIgnoreCase)),
                "economy tray icon embedded");
            Assert(resources.Any(name => name.EndsWith(
                    "tray-fast.ico",
                    StringComparison.OrdinalIgnoreCase)),
                "fast tray icon embedded");
        }

        private static void TestNativeInputLayout()
        {
            Assert(
                WindowsTextInserter.NativeInputStructureSize == 40,
                "native SendInput structure is x64 compatible");
        }

        private static async Task TestBatchTranscriptionAndRetry()
        {
            var calls = 0;
            var handler = new StubHandler(async request =>
            {
                calls++;
                var body = await request.Content.ReadAsStringAsync();
                Assert(request.RequestUri.AbsolutePath ==
                       "/v1/audio/transcriptions", "batch endpoint");
                Assert(body.Contains("gpt-4o-mini-transcribe"),
                    "batch model");
                if (calls == 1)
                {
                    return new HttpResponseMessage(
                        HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("temporary")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Проверочный текст")
                };
            });

            var provider = new OpenAiBatchTranscriptionProvider(
                new HttpClient(handler));
            var result = await provider.TranscribeAsync(
                new TranscriptionRequest
                {
                    Recording = new AudioRecording
                    {
                        Wav16Khz = new byte[] { 1, 2, 3 },
                        Duration = TimeSpan.FromSeconds(4)
                    },
                    Language = "ru",
                    Vocabulary = new[] { "Codex" }
                },
                "test-key",
                CancellationToken.None);

            Assert(calls == 2, "batch retries once");
            Assert(result.Text == "Проверочный текст", "batch result");
            Assert(result.Mode == RecognitionMode.Economy, "batch mode");
        }

        private static async Task TestDeepSeekProcessing()
        {
            var handler = new StubHandler(async request =>
            {
                var body = await request.Content.ReadAsStringAsync();
                Assert(body.Contains("deepseek-v4-flash"),
                    "DeepSeek model");
                Assert(body.Contains("\"type\":\"disabled\""),
                    "DeepSeek thinking disabled");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"message\":{\"content\":\"Готовый текст.\"}}]," +
                        "\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":4}}",
                        Encoding.UTF8,
                        "application/json")
                };
            });

            var processor = new DeepSeekTextPostProcessor(
                new HttpClient(handler));
            var result = await processor.ProcessAsync(
                new TextProcessingRequest
                {
                    Text = "готовый текст",
                    Vocabulary = new[] { "OpenAI" }
                },
                "test-key",
                CancellationToken.None);

            Assert(result.Text == "Готовый текст.", "DeepSeek result");
            Assert(result.InputTokens == 12 && result.OutputTokens == 4,
                "DeepSeek usage");
            Assert(result.EstimatedCostUsd > 0,
                "DeepSeek cost estimate");
        }

        private static string NewTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "SpeechToText.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(
                    "Assertion failed: " + name);
            }
            _passed++;
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>
                _handler;

            public StubHandler(
                Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return _handler(request);
            }
        }
    }
}
