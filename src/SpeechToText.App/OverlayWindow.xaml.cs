using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SpeechToText.Core;
using Forms = System.Windows.Forms;

namespace SpeechToText.App
{
    public partial class OverlayWindow : Window
    {
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoZOrder = 0x0004;

        private readonly DispatcherTimer _timer;
        private DateTime _startedUtc;
        private DateTime _lastSignalUtc;
        private bool _recording;
        private RecognitionMode _recordingMode;
        private CancellationTokenSource _hideDelay;

        public OverlayWindow()
        {
            InitializeComponent();
            _timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(100),
                DispatcherPriority.Background,
                OnTimer,
                Dispatcher);
        }

        public void ShowRecording(RecognitionMode mode)
        {
            RunOnUi(() =>
            {
                CancelHide();
                _recording = true;
                _recordingMode = mode;
                _startedUtc = DateTime.UtcNow;
                _lastSignalUtc = DateTime.UtcNow;
                IconText.Text = "●";
                IconText.Foreground = new SolidColorBrush(
                    Color.FromRgb(255, 107, 107));
                StatusText.Text = "Запись";
                DetailText.Text = ModeLabel(mode);
                TimerText.Visibility = Visibility.Visible;
                LevelBar.Visibility = Visibility.Visible;
                LevelBar.Value = 0;
                TimerText.Text = "00:00";
                ShowWithoutActivation();
                _timer.Start();
            });
        }

        public void UpdateLevel(float level)
        {
            RunOnUi(() =>
            {
                var normalized = Math.Max(0, Math.Min(1, level));
                LevelBar.Value = normalized * 100;
                if (normalized >= 0.012f)
                {
                    _lastSignalUtc = DateTime.UtcNow;
                    if (_recording && StatusText.Text == "Нет входного сигнала")
                    {
                        StatusText.Text = "Запись";
                    }
                }
            });
        }

        public void UpdatePartial(string text)
        {
            RunOnUi(() =>
            {
                if (_recording && !string.IsNullOrWhiteSpace(text))
                {
                    DetailText.Text = text.Replace("\r", " ").Replace("\n", " ");
                }
            });
        }

        public void ShowState(
            WorkflowState state,
            string detail,
            int hideAfterMilliseconds = 0)
        {
            RunOnUi(() =>
            {
                CancelHide();
                _recording = false;
                _timer.Stop();
                TimerText.Visibility = Visibility.Collapsed;
                LevelBar.Visibility = Visibility.Collapsed;
                ApplyStateVisual(state);
                DetailText.Text = detail ?? string.Empty;
                ShowWithoutActivation();
                if (hideAfterMilliseconds > 0)
                {
                    HideLater(hideAfterMilliseconds);
                }
            });
        }

        public void ShowModeToast(RecognitionMode mode)
        {
            RunOnUi(() =>
            {
                CancelHide();
                var resumeRecording = _recording;
                if (!resumeRecording)
                {
                    _timer.Stop();
                }
                IconText.Text = mode == RecognitionMode.Fast ? "⚡" : "₽";
                IconText.Foreground = Brushes.White;
                StatusText.Text = mode == RecognitionMode.Fast
                    ? "⚡ Быстрый режим"
                    : "₽ Экономичный режим";
                DetailText.Text = "Будет применён к следующей диктовке";
                TimerText.Visibility = Visibility.Collapsed;
                LevelBar.Visibility = Visibility.Collapsed;
                ShowWithoutActivation();
                if (resumeRecording)
                {
                    RestoreRecordingLater(1500);
                }
                else
                {
                    HideLater(1500);
                }
            });
        }

        private void ApplyStateVisual(WorkflowState state)
        {
            switch (state)
            {
                case WorkflowState.Transcribing:
                    IconText.Text = "⋯";
                    StatusText.Text = "Распознавание";
                    break;
                case WorkflowState.Editing:
                    IconText.Text = "✎";
                    StatusText.Text = "Исправление текста";
                    break;
                case WorkflowState.Inserting:
                    IconText.Text = "↳";
                    StatusText.Text = "Вставка";
                    break;
                case WorkflowState.Completed:
                    IconText.Text = "✓";
                    StatusText.Text = "Готово";
                    break;
                case WorkflowState.Cancelled:
                    IconText.Text = "×";
                    StatusText.Text = "Отменено";
                    break;
                case WorkflowState.Error:
                    IconText.Text = "!";
                    StatusText.Text = "Ошибка";
                    break;
                default:
                    IconText.Text = "•";
                    StatusText.Text = "Диктовка";
                    break;
            }
            IconText.Foreground = Brushes.White;
        }

        private void OnTimer(object sender, EventArgs eventArgs)
        {
            if (!_recording)
            {
                return;
            }

            var elapsed = DateTime.UtcNow - _startedUtc;
            TimerText.Text = string.Format(
                "{0:00}:{1:00}",
                (int)elapsed.TotalMinutes,
                elapsed.Seconds);

            if (DateTime.UtcNow - _lastSignalUtc > TimeSpan.FromSeconds(2))
            {
                StatusText.Text = "Нет входного сигнала";
            }
        }

        private void ShowWithoutActivation()
        {
            if (!IsVisible)
            {
                Show();
            }

            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
            var helper = new WindowInteropHelper(this);
            var dpi = GetWindowDpi(helper.Handle);
            var scale = dpi / 96d;
            var width = (int)Math.Ceiling(ActualWidth * scale);
            var height = (int)Math.Ceiling(ActualHeight * scale);
            var left = screen.WorkingArea.Left +
                       (screen.WorkingArea.Width - width) / 2;
            var top = screen.WorkingArea.Bottom - height - 34;
            SetWindowPos(
                helper.Handle,
                IntPtr.Zero,
                left,
                top,
                width,
                height,
                SwpNoActivate | SwpNoZOrder);
        }

        private void HideLater(int milliseconds)
        {
            _hideDelay = new CancellationTokenSource();
            var token = _hideDelay.Token;
            Task.Delay(milliseconds, token).ContinueWith(
                task =>
                {
                    if (!task.IsCanceled)
                    {
                        Dispatcher.BeginInvoke(new Action(Hide));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void RestoreRecordingLater(int milliseconds)
        {
            _hideDelay = new CancellationTokenSource();
            var token = _hideDelay.Token;
            Task.Delay(milliseconds, token).ContinueWith(
                task =>
                {
                    if (task.IsCanceled)
                    {
                        return;
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_recording)
                        {
                            return;
                        }

                        IconText.Text = "●";
                        IconText.Foreground = new SolidColorBrush(
                            Color.FromRgb(255, 107, 107));
                        StatusText.Text = "Запись";
                        DetailText.Text = ModeLabel(_recordingMode);
                        TimerText.Visibility = Visibility.Visible;
                        LevelBar.Visibility = Visibility.Visible;
                    }));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void CancelHide()
        {
            _hideDelay?.Cancel();
            _hideDelay?.Dispose();
            _hideDelay = null;
        }

        private static string ModeLabel(RecognitionMode mode)
        {
            return mode == RecognitionMode.Fast
                ? "⚡ Быстрый режим"
                : "₽ Экономичный режим";
        }

        private void RunOnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.BeginInvoke(action);
            }
        }

        private static uint GetWindowDpi(IntPtr window)
        {
            try
            {
                var dpi = GetDpiForWindow(window);
                return dpi == 0 ? 96u : dpi;
            }
            catch (EntryPointNotFoundException)
            {
                return 96;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            CancelHide();
            _timer.Stop();
            base.OnClosed(e);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);
    }
}
