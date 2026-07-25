using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using SpeechToText.Core;

namespace SpeechToText.App
{
    public sealed class AutoStartService : IAutoStartService
    {
        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SpeechToText";

        private readonly string _executablePath;

        public AutoStartService(string executablePath = null)
        {
            _executablePath = Path.GetFullPath(
                executablePath ??
                Assembly.GetEntryAssembly().Location);
        }

        public bool IsEnabled => GetStatus() == AutoStartStatus.Enabled;

        public AutoStartStatus GetStatus()
        {
            var value = ReadRawValue();
            if (string.IsNullOrWhiteSpace(value))
            {
                return AutoStartStatus.Disabled;
            }

            string registeredPath;
            if (!TryParseExecutablePath(value, out registeredPath) ||
                !File.Exists(registeredPath))
            {
                return AutoStartStatus.Broken;
            }

            return string.Equals(
                Path.GetFullPath(registeredPath),
                _executablePath,
                StringComparison.OrdinalIgnoreCase)
                ? AutoStartStatus.Enabled
                : AutoStartStatus.Broken;
        }

        public void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enabled)
                {
                    key.SetValue(
                        ValueName,
                        BuildCommand(_executablePath),
                        RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }

        public void RepairIfNeeded()
        {
            if (ReadRawValue() != null)
            {
                SetEnabled(true);
            }
        }

        public AutoStartStatus SynchronizeOnManualLaunch()
        {
            var value = ReadRawValue();
            if (string.IsNullOrWhiteSpace(value))
            {
                return AutoStartStatus.Disabled;
            }

            string registeredPath;
            if (!TryParseExecutablePath(value, out registeredPath))
            {
                return AutoStartStatus.Broken;
            }

            if (string.Equals(
                Path.GetFullPath(registeredPath),
                _executablePath,
                StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(registeredPath)
                    ? AutoStartStatus.Enabled
                    : AutoStartStatus.Broken;
            }

            if (string.Equals(
                Path.GetFileName(registeredPath),
                Path.GetFileName(_executablePath),
                StringComparison.OrdinalIgnoreCase))
            {
                SetEnabled(true);
                return AutoStartStatus.Enabled;
            }

            return AutoStartStatus.Broken;
        }

        internal string ReadRawValue()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                return key?.GetValue(ValueName) as string;
            }
        }

        internal static string BuildCommand(string executablePath)
        {
            return "\"" + executablePath + "\" --background";
        }

        internal static bool TryParseExecutablePath(
            string command,
            out string executablePath)
        {
            executablePath = null;
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            var trimmed = command.Trim();
            if (!trimmed.StartsWith("\"", StringComparison.Ordinal))
            {
                return false;
            }

            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return false;
            }

            var arguments = trimmed.Substring(closingQuote + 1).Trim();
            if (!string.Equals(
                arguments,
                "--background",
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            executablePath = trimmed.Substring(1, closingQuote - 1);
            return Path.IsPathRooted(executablePath);
        }
    }
}
