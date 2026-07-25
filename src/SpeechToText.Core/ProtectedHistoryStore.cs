using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SpeechToText.Core
{
    public sealed class ProtectedHistoryStore : IHistoryStore
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("SpeechToText.Personal.History.v1");

        private readonly object _sync = new object();
        private readonly string _path;

        public ProtectedHistoryStore(string rootDirectory = null)
        {
            var root = rootDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpeechToText");
            _path = Path.Combine(root, "history.bin");
        }

        public IReadOnlyList<HistoryEntry> Load()
        {
            lock (_sync)
            {
                return LoadUnsafe();
            }
        }

        public void Append(HistoryEntry entry)
        {
            lock (_sync)
            {
                var entries = LoadUnsafe().ToList();
                entries.Insert(0, entry);
                SaveUnsafe(entries.Take(50).ToList());
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
        }

        public UsageSummary Summarize()
        {
            var entries = Load();
            return new UsageSummary
            {
                Count = entries.Count,
                AudioMinutes = entries.Sum(x => x.AudioSeconds) / 60d,
                AverageLatencyMilliseconds = entries.Count == 0
                    ? 0
                    : entries.Average(x => x.TotalLatencyMilliseconds),
                EstimatedCostUsd = entries.Sum(x => x.EstimatedCostUsd)
            };
        }

        private List<HistoryEntry> LoadUnsafe()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new List<HistoryEntry>();
                }

                var protectedBytes = File.ReadAllBytes(_path);
                var plain = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plain);
                return new JavaScriptSerializer().Deserialize<List<HistoryEntry>>(json)
                    ?? new List<HistoryEntry>();
            }
            catch
            {
                return new List<HistoryEntry>();
            }
        }

        private void SaveUnsafe(List<HistoryEntry> entries)
        {
            var directory = Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory);
            var json = new JavaScriptSerializer().Serialize(entries);
            var plain = Encoding.UTF8.GetBytes(json);
            var protectedBytes = ProtectedData.Protect(
                plain,
                Entropy,
                DataProtectionScope.CurrentUser);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, null);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
    }
}
