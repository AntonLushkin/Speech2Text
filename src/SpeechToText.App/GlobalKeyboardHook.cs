using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace SpeechToText.App
{
    public sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const uint LlkhfInjected = 0x00000010;
        private const int VkEscape = 0x1B;

        private readonly HashSet<int> _pressed = new HashSet<int>();
        private readonly HashSet<int> _suppressedKeys = new HashSet<int>();
        private readonly HookProc _callback;
        private IntPtr _hook;
        private HashSet<int> _recordKeys;
        private HashSet<int> _modeKeys;
        private bool _recordLatched;
        private bool _modeLatched;

        public GlobalKeyboardHook(
            string recordHotkey,
            string modeHotkey)
        {
            _recordKeys = ParseHotkey(recordHotkey, "LControl+LAlt");
            _modeKeys = ParseHotkey(modeHotkey, "LControl+Space");
            _callback = HookCallback;
        }

        public event EventHandler RecordingRequested;
        public event EventHandler RecordingReleased;
        public event EventHandler ModeToggleRequested;
        public event EventHandler CancelRequested;

        public void Start()
        {
            if (_hook != IntPtr.Zero)
            {
                return;
            }

            using (var process = Process.GetCurrentProcess())
            using (var module = process.MainModule)
            {
                _hook = SetWindowsHookEx(
                    WhKeyboardLl,
                    _callback,
                    GetModuleHandle(module.ModuleName),
                    0);
            }

            if (_hook == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Не удалось зарегистрировать глобальные горячие клавиши.");
            }
        }

        public void UpdateHotkeys(
            string recordHotkey,
            string modeHotkey)
        {
            _recordKeys = ParseHotkey(recordHotkey, "LControl+LAlt");
            _modeKeys = ParseHotkey(modeHotkey, "LControl+Space");
            _recordLatched = false;
            _modeLatched = false;
            _pressed.Clear();
            _suppressedKeys.Clear();
        }

        private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
        {
            if (code < 0)
            {
                return CallNextHookEx(_hook, code, message, data);
            }

            var keyboard = (KeyboardData)Marshal.PtrToStructure(
                data,
                typeof(KeyboardData));
            if ((keyboard.Flags & LlkhfInjected) != 0)
            {
                return CallNextHookEx(_hook, code, message, data);
            }

            var isDown = message == (IntPtr)WmKeyDown ||
                         message == (IntPtr)WmSysKeyDown;
            var isUp = message == (IntPtr)WmKeyUp ||
                       message == (IntPtr)WmSysKeyUp;
            if (!isDown && !isUp)
            {
                return CallNextHookEx(_hook, code, message, data);
            }

            var key = (int)keyboard.VirtualKey;
            if (isDown)
            {
                _pressed.Add(key);

                if (_recordLatched && key == VkEscape)
                {
                    CancelRequested?.Invoke(this, EventArgs.Empty);
                    _recordLatched = false;
                    _suppressedKeys.Add(key);
                    return new IntPtr(1);
                }

                if (!_modeLatched && ContainsAll(_modeKeys))
                {
                    _modeLatched = true;
                    ModeToggleRequested?.Invoke(this, EventArgs.Empty);
                    _suppressedKeys.Add(key);
                    return new IntPtr(1);
                }

                if (!_recordLatched && ContainsAll(_recordKeys))
                {
                    _recordLatched = true;
                    RecordingRequested?.Invoke(this, EventArgs.Empty);
                    _suppressedKeys.Add(key);
                    return new IntPtr(1);
                }

                if (_recordLatched && _recordKeys.Contains(key) &&
                    _suppressedKeys.Contains(key))
                {
                    return new IntPtr(1);
                }
            }
            else
            {
                var wasRecordKey = _recordKeys.Contains(key);
                var wasSuppressed = _suppressedKeys.Remove(key);
                _pressed.Remove(key);

                if (_modeLatched && !_modeKeys.All(_pressed.Contains))
                {
                    _modeLatched = false;
                }

                if (_recordLatched && wasRecordKey)
                {
                    _recordLatched = false;
                    RecordingReleased?.Invoke(this, EventArgs.Empty);
                }

                if (wasSuppressed)
                {
                    return new IntPtr(1);
                }
            }

            return CallNextHookEx(_hook, code, message, data);
        }

        private bool ContainsAll(IEnumerable<int> keys)
        {
            return keys.All(_pressed.Contains);
        }

        internal static HashSet<int> ParseHotkey(
            string text,
            string fallback)
        {
            var result = ParseHotkeyCore(text);
            return result.Count >= 2
                ? result
                : ParseHotkeyCore(fallback);
        }

        private static HashSet<int> ParseHotkeyCore(string text)
        {
            var result = new HashSet<int>();
            foreach (var raw in (text ?? string.Empty).Split('+'))
            {
                int key;
                if (KeyNames.TryGetValue(raw.Trim(), out key))
                {
                    result.Add(key);
                }
            }
            return result;
        }

        private static readonly Dictionary<string, int> KeyNames =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["RControl"] = 0xA3,
                ["RAlt"] = 0xA5,
                ["RShift"] = 0xA1,
                ["LControl"] = 0xA2,
                ["LAlt"] = 0xA4,
                ["LShift"] = 0xA0,
                ["Space"] = 0x20
            };

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardData
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook,
            HookProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr message,
            IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);
    }
}
