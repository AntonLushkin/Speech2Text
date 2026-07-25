using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SpeechToText.Core;

namespace SpeechToText.App
{
    public sealed class TrayController : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Icon _economyIcon;
        private readonly Icon _fastIcon;
        private readonly ToolStripMenuItem _modeEconomy;
        private readonly ToolStripMenuItem _modeFast;
        private readonly ToolStripMenuItem _autoStart;
        private readonly ToolStripMenuItem _microphones;
        private readonly ToolStripMenuItem _retryFailed;
        private readonly ToolStripMenuItem _discardFailed;

        public TrayController()
        {
            _economyIcon = LoadEmbeddedIcon("tray-economy.ico");
            _fastIcon = LoadEmbeddedIcon("tray-fast.ico");
            var menu = new ContextMenuStrip();
            _modeEconomy = new ToolStripMenuItem("₽ Экономичный режим");
            _modeFast = new ToolStripMenuItem("⚡ Быстрый режим");
            _autoStart = new ToolStripMenuItem("Запускать вместе с Windows");
            _microphones = new ToolStripMenuItem("Микрофон");
            _retryFailed = new ToolStripMenuItem(
                "Повторить неудачную запись")
            {
                Enabled = false
            };
            _discardFailed = new ToolStripMenuItem(
                "Удалить неудачную запись")
            {
                Enabled = false
            };

            _modeEconomy.Click += (sender, args) =>
                ModeChangeRequested?.Invoke(this, RecognitionMode.Economy);
            _modeFast.Click += (sender, args) =>
                ModeChangeRequested?.Invoke(this, RecognitionMode.Fast);
            _autoStart.Click += (sender, args) =>
                AutoStartToggleRequested?.Invoke(this, EventArgs.Empty);
            _retryFailed.Click += (sender, args) =>
                RetryFailedRequested?.Invoke(this, EventArgs.Empty);
            _discardFailed.Click += (sender, args) =>
                DiscardFailedRequested?.Invoke(this, EventArgs.Empty);

            var settings = new ToolStripMenuItem("Настройки");
            settings.Click += (sender, args) =>
                SettingsRequested?.Invoke(this, EventArgs.Empty);
            var exit = new ToolStripMenuItem("Выход");
            exit.Click += (sender, args) =>
                ExitRequested?.Invoke(this, EventArgs.Empty);

            menu.Items.Add(_modeEconomy);
            menu.Items.Add(_modeFast);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_microphones);
            menu.Items.Add(_autoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_retryFailed);
            menu.Items.Add(_discardFailed);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(settings);
            menu.Items.Add(exit);

            _icon = new NotifyIcon
            {
                Visible = true,
                ContextMenuStrip = menu,
                Icon = _economyIcon,
                Text = "Диктовка — экономичный режим"
            };
            _icon.DoubleClick += (sender, args) =>
                SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler SettingsRequested;
        public event EventHandler ExitRequested;
        public event EventHandler AutoStartToggleRequested;
        public event EventHandler RetryFailedRequested;
        public event EventHandler DiscardFailedRequested;
        public event EventHandler<RecognitionMode> ModeChangeRequested;
        public event EventHandler<string> MicrophoneChangeRequested;

        public void Refresh(
            AppSettings settings,
            AutoStartStatus autoStartStatus,
            IReadOnlyList<MicrophoneInfo> microphones)
        {
            _modeEconomy.Checked =
                settings.Mode == RecognitionMode.Economy;
            _modeFast.Checked = settings.Mode == RecognitionMode.Fast;
            _autoStart.Checked =
                autoStartStatus == AutoStartStatus.Enabled;

            _icon.Icon = settings.Mode == RecognitionMode.Fast
                ? _fastIcon
                : _economyIcon;
            _icon.Text = settings.Mode == RecognitionMode.Fast
                ? "Диктовка — быстрый режим"
                : "Диктовка — экономичный режим";

            _microphones.DropDownItems.Clear();
            foreach (var microphone in microphones ??
                     Enumerable.Empty<MicrophoneInfo>())
            {
                var item = new ToolStripMenuItem(microphone.Name)
                {
                    Checked = string.Equals(
                        microphone.Id ?? string.Empty,
                        settings.MicrophoneId ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase),
                    Tag = microphone.Id ?? string.Empty
                };
                item.Click += (sender, args) =>
                {
                    var selected = (ToolStripMenuItem)sender;
                    MicrophoneChangeRequested?.Invoke(
                        this,
                        Convert.ToString(selected.Tag));
                };
                _microphones.DropDownItems.Add(item);
            }
        }

        public void ShowBalloon(string title, string message, bool error = false)
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = error
                ? ToolTipIcon.Error
                : ToolTipIcon.Info;
            _icon.ShowBalloonTip(2500);
        }

        public void SetRecoveryAvailable(bool available)
        {
            _retryFailed.Enabled = available;
            _discardFailed.Enabled = available;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Icon = null;
            _icon.Dispose();
            _economyIcon.Dispose();
            _fastIcon.Dispose();
        }

        private static Icon LoadEmbeddedIcon(string fileName)
        {
            var assembly = typeof(TrayController).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(
                    fileName,
                    StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                throw new InvalidOperationException(
                    "Не найден встроенный значок " + fileName + ".");
            }

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            using (var icon = new Icon(stream))
            {
                return (Icon)icon.Clone();
            }
        }
    }
}
