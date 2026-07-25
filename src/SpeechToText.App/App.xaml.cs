using System;
using System.Linq;
using System.Net;
using System.Windows;
using SpeechToText.Core;

namespace SpeechToText.App
{
    public partial class App : Application
    {
        private SingleInstanceGuard _singleInstance;
        private AppSettingsStore _settingsStore;
        private AppSettings _settings;
        private WindowsCredentialStore _credentialStore;
        private AutoStartService _autoStart;
        private ProtectedHistoryStore _history;
        private NAudioCaptureService _audioCapture;
        private OverlayWindow _overlay;
        private TrayController _tray;
        private GlobalKeyboardHook _keyboard;
        private SpeechWorkflowController _workflow;
        private SettingsWindow _settingsWindow;
        private bool _isExiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            _singleInstance = SingleInstanceGuard.Acquire();
            if (!_singleInstance.IsPrimary)
            {
                Shutdown();
                return;
            }

            try
            {
                var background = e.Args.Any(argument => string.Equals(
                    argument,
                    "--background",
                    StringComparison.OrdinalIgnoreCase));

                _settingsStore = new AppSettingsStore();
                _settings = _settingsStore.Load();
                _credentialStore = new WindowsCredentialStore();
                _autoStart = new AutoStartService();
                _history = new ProtectedHistoryStore();
                _audioCapture = new NAudioCaptureService();
                _overlay = new OverlayWindow();
                _tray = new TrayController();
                _keyboard = new GlobalKeyboardHook(
                    _settings.RecordHotkey,
                    _settings.ModeHotkey);
                _workflow = new SpeechWorkflowController(
                    _settings,
                    _audioCapture,
                    new OpenAiBatchTranscriptionProvider(),
                    new OpenAiRealtimeTranscriptionSessionFactory(),
                    new DeepSeekTextPostProcessor(),
                    new WindowsTextInserter(Dispatcher),
                    _credentialStore,
                    _history,
                    _overlay,
                    _tray);

                WireEvents();
                _keyboard.Start();
                RefreshTray();
                _singleInstance.Listen(Dispatcher, ShowSettings);

                if (!background)
                {
                    _autoStart.SynchronizeOnManualLaunch();
                    ShowSettings();
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "Не удалось запустить диктовщик: " + exception.Message,
                    "pis.etc",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void WireEvents()
        {
            _keyboard.RecordingRequested += async (sender, args) =>
                await _workflow.BeginRecordingAsync();
            _keyboard.RecordingReleased += async (sender, args) =>
                await _workflow.FinishRecordingAsync();
            _keyboard.CancelRequested += async (sender, args) =>
                await _workflow.CancelAsync();
            _keyboard.ModeToggleRequested += (sender, args) =>
                SetMode(_settings.Mode == RecognitionMode.Fast
                    ? RecognitionMode.Economy
                    : RecognitionMode.Fast);

            _tray.SettingsRequested += (sender, args) => ShowSettings();
            _tray.ExitRequested += (sender, args) => ExitApplication();
            _tray.ModeChangeRequested += (sender, mode) => SetMode(mode);
            _tray.MicrophoneChangeRequested += (sender, microphoneId) =>
            {
                _settings.MicrophoneId = microphoneId ?? string.Empty;
                _settingsStore.Save(_settings);
                RefreshTray();
            };
            _tray.AutoStartToggleRequested += (sender, args) =>
            {
                try
                {
                    _autoStart.SetEnabled(!_autoStart.IsEnabled);
                    RefreshTray();
                }
                catch (Exception exception)
                {
                    _tray.ShowBalloon(
                        "Автозапуск",
                        "Не удалось изменить настройку: " + exception.Message,
                        error: true);
                }
            };
            _tray.RetryFailedRequested += async (sender, args) =>
                await _workflow.RetryFailedAsync();
            _tray.DiscardFailedRequested += (sender, args) =>
                _workflow.DiscardFailedAudio();

            _audioCapture.DevicesChanged += (sender, args) =>
                Dispatcher.BeginInvoke(new Action(HandleDeviceChange));
        }

        private void SetMode(RecognitionMode mode)
        {
            if (_settings.Mode == mode)
            {
                return;
            }

            _settings.Mode = mode;
            _settingsStore.Save(_settings);
            _workflow.UpdateSettings(_settings);
            RefreshTray();
            _settingsWindow?.SetModeIcon(mode);
            if (_settings.ShowOverlay)
            {
                _overlay.ShowModeToast(mode);
            }
        }

        private void HandleDeviceChange()
        {
            var microphones = _audioCapture.GetMicrophones();
            if (!string.IsNullOrWhiteSpace(_settings.MicrophoneId) &&
                !microphones.Any(item => string.Equals(
                    item.Id,
                    _settings.MicrophoneId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                _settings.MicrophoneId = string.Empty;
                _settingsStore.Save(_settings);
                _workflow.UpdateSettings(_settings);
                _tray.ShowBalloon(
                    "Микрофон отключён",
                    "Выбранный микрофон недоступен. Используется системный.");
            }
            RefreshTray();
        }

        private void ApplySettings(AppSettings settings)
        {
            _settings = settings;
            _workflow.UpdateSettings(settings);
            _keyboard.UpdateHotkeys(
                settings.RecordHotkey,
                settings.ModeHotkey);
            RefreshTray();
            _settingsWindow?.SetModeIcon(settings.Mode);
        }

        private void ShowSettings()
        {
            if (_isExiting)
            {
                return;
            }

            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(
                    _settingsStore,
                    _credentialStore,
                    _autoStart,
                    _audioCapture,
                    _history,
                    ApplySettings);
            }

            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }
            _settingsWindow.RefreshFromStores();
            _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }

        private void RefreshTray()
        {
            _tray.Refresh(
                _settings,
                _autoStart.GetStatus(),
                _audioCapture.GetMicrophones());
        }

        private void ExitApplication()
        {
            _isExiting = true;
            _settingsWindow?.AllowClose();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _isExiting = true;
            _workflow?.Dispose();
            _keyboard?.Dispose();
            _tray?.Dispose();
            _overlay?.Close();
            _audioCapture?.Dispose();
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
