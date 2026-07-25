using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpeechToText.Core;

namespace SpeechToText.App
{
    public partial class SettingsWindow : Window
    {
        public const string OpenAiCredentialName = "SpeechToText/OpenAI";
        public const string DeepSeekCredentialName = "SpeechToText/DeepSeek";

        private readonly AppSettingsStore _settingsStore;
        private readonly ICredentialStore _credentialStore;
        private readonly AutoStartService _autoStart;
        private readonly IAudioCaptureService _audioCapture;
        private readonly IHistoryStore _historyStore;
        private readonly Action<AppSettings> _settingsApplied;
        private readonly ImageSource _economyWindowIcon;
        private readonly ImageSource _fastWindowIcon;
        private bool _allowClose;
        private bool _testingMicrophone;
        private bool _showingSavedState;

        public SettingsWindow(
            AppSettingsStore settingsStore,
            ICredentialStore credentialStore,
            AutoStartService autoStart,
            IAudioCaptureService audioCapture,
            IHistoryStore historyStore,
            Action<AppSettings> settingsApplied)
        {
            InitializeComponent();
            _economyWindowIcon = LoadWindowIcon("tray-economy.ico");
            _fastWindowIcon = LoadWindowIcon("tray-fast.ico");
            _settingsStore = settingsStore;
            _credentialStore = credentialStore;
            _autoStart = autoStart;
            _audioCapture = audioCapture;
            _historyStore = historyStore;
            _settingsApplied = settingsApplied;
            _audioCapture.DevicesChanged += OnDevicesChanged;
            _audioCapture.LevelChanged += OnTestLevelChanged;
        }

        public void RefreshFromStores()
        {
            var settings = _settingsStore.Load();
            SetModeIcon(settings.Mode);
            EconomyMode.IsChecked =
                settings.Mode == RecognitionMode.Economy;
            FastMode.IsChecked = settings.Mode == RecognitionMode.Fast;
            DeepSeekEnabled.IsChecked = settings.EnableDeepSeek;
            OverlayEnabled.IsChecked = settings.ShowOverlay;
            PartialTextEnabled.IsChecked = settings.ShowPartialText;
            SoundsEnabled.IsChecked = settings.EnableSounds;
            SelectByTag(
                RecordHotkeyBox,
                settings.RecordHotkey,
                "LControl+LAlt");
            SelectByTag(
                ModeHotkeyBox,
                settings.ModeHotkey,
                "LControl+Space");
            VocabularyBox.Text = string.Join(
                Environment.NewLine,
                settings.Vocabulary ?? new List<string>());

            RefreshMicrophones(settings.MicrophoneId);
            RefreshAutoStart();
            RefreshHistory();

            try
            {
                OpenAiKeyBox.Password =
                    _credentialStore.Read(OpenAiCredentialName);
                DeepSeekKeyBox.Password =
                    _credentialStore.Read(DeepSeekCredentialName);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "Не удалось прочитать сохранённые API-ключи: " +
                    exception.Message,
                    "API-ключи",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        public void SetModeIcon(RecognitionMode mode)
        {
            Icon = mode == RecognitionMode.Fast
                ? _fastWindowIcon
                : _economyWindowIcon;
        }

        public void AllowClose()
        {
            _allowClose = true;
            Close();
        }

        private void RefreshAutoStart()
        {
            var status = _autoStart.GetStatus();
            if (status == AutoStartStatus.Broken)
            {
                AutoStartEnabled.IsChecked = false;
                AutoStartStatusText.Text =
                    "Запись автозапуска повреждена или ведёт к отсутствующему файлу.";
                var restore = MessageBox.Show(
                    this,
                    "Запись автозапуска повреждена или путь к программе больше " +
                    "не существует. Восстановить её для текущего расположения?",
                    "Восстановление автозапуска",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (restore == MessageBoxResult.Yes)
                {
                    _autoStart.SetEnabled(true);
                    status = AutoStartStatus.Enabled;
                }
            }

            AutoStartEnabled.IsChecked =
                status == AutoStartStatus.Enabled;
            AutoStartStatusText.Text =
                status == AutoStartStatus.Enabled
                    ? "Автозапуск настроен для текущего расположения программы."
                    : "Автозапуск выключен.";
        }

        private void RefreshMicrophones(string selectedId)
        {
            var microphones = _audioCapture.GetMicrophones();
            MicrophoneBox.ItemsSource = microphones;
            MicrophoneBox.SelectedItem = microphones.FirstOrDefault(
                item => string.Equals(
                    item.Id ?? string.Empty,
                    selectedId ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)) ?? microphones.First();

            if (!string.IsNullOrWhiteSpace(selectedId) &&
                !microphones.Any(item => string.Equals(
                    item.Id,
                    selectedId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                MicrophoneStatus.Text =
                    "Выбранный микрофон недоступен. Используется системный.";
            }
            else
            {
                MicrophoneStatus.Text = string.Empty;
            }
        }

        private async void TestMicrophone_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_testingMicrophone)
            {
                return;
            }

            _testingMicrophone = true;
            TestMicrophoneButton.IsEnabled = false;
            TestLevel.Value = 0;
            MicrophoneStatus.Text = "Говорите…";
            try
            {
                var microphone = MicrophoneBox.SelectedItem as MicrophoneInfo;
                await _audioCapture.StartAsync(
                    microphone?.Id ?? string.Empty,
                    CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(3));
                var recording = await _audioCapture.StopAsync(
                    CancellationToken.None);
                MicrophoneStatus.Text = recording.PeakLevel >= 0.012f
                    ? "Микрофон работает."
                    : "Сигнал почти не слышен. Проверьте микрофон и уровень входа.";
            }
            catch (Exception exception)
            {
                MicrophoneStatus.Text =
                    "Проверка не удалась: " + exception.Message;
            }
            finally
            {
                _testingMicrophone = false;
                TestMicrophoneButton.IsEnabled = true;
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_showingSavedState)
            {
                return;
            }

            _showingSavedState = true;
            var originalBackground = SaveButton.Background;
            var originalForeground = SaveButton.Foreground;
            var originalBorder = SaveButton.BorderBrush;
            try
            {
                SaveButton.IsHitTestVisible = false;
                var microphone = MicrophoneBox.SelectedItem as MicrophoneInfo;
                var settings = new AppSettings
                {
                    Mode = FastMode.IsChecked == true
                        ? RecognitionMode.Fast
                        : RecognitionMode.Economy,
                    MicrophoneId = microphone?.Id ?? string.Empty,
                    Language = "ru",
                    EnableDeepSeek = DeepSeekEnabled.IsChecked == true,
                    ShowOverlay = OverlayEnabled.IsChecked == true,
                    ShowPartialText = PartialTextEnabled.IsChecked == true,
                    EnableSounds = SoundsEnabled.IsChecked == true,
                    RecordHotkey = SelectedTag(
                        RecordHotkeyBox,
                        "LControl+LAlt"),
                    ModeHotkey = SelectedTag(
                        ModeHotkeyBox,
                        "LControl+Space"),
                    Vocabulary = VocabularyBox.Text
                        .Split(new[] { "\r\n", "\n" },
                            StringSplitOptions.RemoveEmptyEntries)
                        .Select(item => item.Trim())
                        .Where(item => item.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                _settingsStore.Save(settings);
                _credentialStore.Write(
                    OpenAiCredentialName,
                    OpenAiKeyBox.Password.Trim());
                _credentialStore.Write(
                    DeepSeekCredentialName,
                    DeepSeekKeyBox.Password.Trim());
                _autoStart.SetEnabled(AutoStartEnabled.IsChecked == true);
                _settingsApplied(settings);
                SaveButton.Content = "Сохранено";
                SaveButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(46, 173, 102));
                SaveButton.BorderBrush = SaveButton.Background;
                SaveButton.Foreground = System.Windows.Media.Brushes.White;
                await Task.Delay(1500);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "Не удалось сохранить настройки: " + exception.Message,
                    "Настройки",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SaveButton.Content = "Сохранить";
                SaveButton.Background = originalBackground;
                SaveButton.Foreground = originalForeground;
                SaveButton.BorderBrush = originalBorder;
                SaveButton.IsHitTestVisible = true;
                _showingSavedState = false;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                    this,
                    "Удалить всю историю диктовок? Восстановить её будет нельзя.",
                    "Очистка истории",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            _historyStore.Clear();
            RefreshHistory();
        }

        private void RefreshHistory()
        {
            HistoryGrid.ItemsSource = _historyStore.Load()
                .Select(item => new HistoryRow
                {
                    LocalTime = item.TimestampUtc.ToLocalTime()
                        .ToString("dd.MM.yyyy HH:mm"),
                    Mode = item.Mode == RecognitionMode.Fast
                        ? "Быстрый"
                        : "Экономичный",
                    Text = Shorten(
                        string.IsNullOrWhiteSpace(item.CorrectedText)
                            ? item.RawText
                            : item.CorrectedText),
                    Status = item.Status
                })
                .ToList();

            var summary = _historyStore.Summarize();
            StatisticsText.Text = string.Format(
                "{0} диктовок  •  {1:0.0} мин  •  средняя задержка {2:0.0} с  •  ≈ ${3:0.0000}",
                summary.Count,
                summary.AudioMinutes,
                summary.AverageLatencyMilliseconds / 1000d,
                summary.EstimatedCostUsd);
        }

        private void OnDevicesChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var selected = MicrophoneBox.SelectedItem as MicrophoneInfo;
                RefreshMicrophones(selected?.Id ?? string.Empty);
            }));
        }

        private void OnTestLevelChanged(object sender, float level)
        {
            if (!_testingMicrophone)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(
                () => TestLevel.Value = Math.Max(0, Math.Min(100, level * 100))));
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _audioCapture.DevicesChanged -= OnDevicesChanged;
            _audioCapture.LevelChanged -= OnTestLevelChanged;
            base.OnClosed(e);
        }

        private static void SelectByTag(
            ComboBox box,
            string value,
            string fallback)
        {
            box.SelectedItem = box.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    Convert.ToString(item.Tag),
                    value,
                    StringComparison.OrdinalIgnoreCase))
                ?? box.Items
                    .OfType<ComboBoxItem>()
                    .First(item => string.Equals(
                        Convert.ToString(item.Tag),
                        fallback,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static string SelectedTag(ComboBox box, string fallback)
        {
            return Convert.ToString(
                       (box.SelectedItem as ComboBoxItem)?.Tag) ??
                   fallback;
        }

        private static ImageSource LoadWindowIcon(string fileName)
        {
            var assembly = typeof(SettingsWindow).Assembly;
            var resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(
                    fileName,
                    StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                throw new InvalidOperationException(
                    "Не найден ресурс иконки: " + fileName);
            }

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        "Не удалось открыть ресурс иконки: " + fileName);
                }

                var decoder = new IconBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var icon = decoder.Frames
                    .OrderByDescending(frame => frame.PixelWidth)
                    .First();
                icon.Freeze();
                return icon;
            }
        }

        private static string Shorten(string text)
        {
            var oneLine = (text ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            return oneLine.Length <= 180
                ? oneLine
                : oneLine.Substring(0, 177) + "…";
        }

        private sealed class HistoryRow
        {
            public string LocalTime { get; set; }
            public string Mode { get; set; }
            public string Text { get; set; }
            public string Status { get; set; }
        }
    }
}
