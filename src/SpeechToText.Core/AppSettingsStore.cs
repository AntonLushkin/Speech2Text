using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SpeechToText.Core
{
    public sealed class AppSettingsStore
    {
        private readonly string _path;

        public AppSettingsStore(string rootDirectory = null)
        {
            var root = rootDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpeechToText");
            _path = Path.Combine(root, "settings.json");
        }

        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(_path, Encoding.UTF8);
                var settings = new JavaScriptSerializer().Deserialize<AppSettings>(json);
                settings.Vocabulary = settings.Vocabulary ?? new System.Collections.Generic.List<string>();
                settings.Language = string.IsNullOrWhiteSpace(settings.Language) ? "ru" : settings.Language;
                settings.RecordHotkey = UseLeftSideKeys(
                    settings.RecordHotkey,
                    "LControl+LAlt");
                settings.ModeHotkey = UseLeftSideKeys(
                    settings.ModeHotkey,
                    "LControl+Space");
                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            var directory = Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory);
            var json = new JavaScriptSerializer().Serialize(settings);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, null);
            }
            else
            {
                File.Move(temp, _path);
            }
        }

        private static string UseLeftSideKeys(string hotkey, string fallback)
        {
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                return fallback;
            }

            return hotkey
                .Replace("RControl", "LControl")
                .Replace("RAlt", "LAlt")
                .Replace("RShift", "LShift");
        }
    }
}
