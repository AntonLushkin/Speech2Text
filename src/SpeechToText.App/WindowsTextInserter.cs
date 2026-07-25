using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SpeechToText.Core;

namespace SpeechToText.App
{
    public sealed class WindowsTextInserter : ITextInserter
    {
        private const ushort VkControl = 0x11;
        private const ushort VkV = 0x56;
        private const uint InputKeyboard = 1;
        private const uint KeyUp = 0x0002;

        private readonly Dispatcher _dispatcher;

        internal static int NativeInputStructureSize =>
            Marshal.SizeOf(typeof(Input));

        public WindowsTextInserter(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public IntPtr CaptureTargetWindow()
        {
            return GetForegroundWindow();
        }

        public async Task<InsertResult> InsertAsync(
            IntPtr targetWindow,
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _dispatcher.InvokeAsync(
                () => SetClipboardWithRetry(text ?? string.Empty),
                DispatcherPriority.Send,
                cancellationToken);

            if (targetWindow == IntPtr.Zero ||
                GetForegroundWindow() != targetWindow)
            {
                return new InsertResult
                {
                    Inserted = false,
                    CopiedToClipboard = true,
                    Message = "Окно изменилось — текст оставлен в буфере обмена."
                };
            }

            var inputs = new[]
            {
                KeyboardInput(VkControl, 0),
                KeyboardInput(VkV, 0),
                KeyboardInput(VkV, KeyUp),
                KeyboardInput(VkControl, KeyUp)
            };

            var sent = SendInput(
                (uint)inputs.Length,
                inputs,
                NativeInputStructureSize);
            if (sent != inputs.Length)
            {
                return new InsertResult
                {
                    Inserted = false,
                    CopiedToClipboard = true,
                    Message =
                        "Не удалось автоматически вставить текст — он оставлен в буфере обмена."
                };
            }

            return new InsertResult
            {
                Inserted = true,
                CopiedToClipboard = true,
                Message = "Текст вставлен."
            };
        }

        private static void SetClipboardWithRetry(string text)
        {
            Exception last = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    return;
                }
                catch (Exception exception)
                {
                    last = exception;
                    Thread.Sleep(40 * (attempt + 1));
                }
            }

            throw new InvalidOperationException(
                "Не удалось открыть буфер обмена.",
                last);
        }

        private static Input KeyboardInput(ushort virtualKey, uint flags)
        {
            return new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInputData
                    {
                        VirtualKey = virtualKey,
                        Flags = flags
                    }
                }
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KeyboardInputData Keyboard;

            [FieldOffset(0)]
            public MouseInputData Mouse;

            [FieldOffset(0)]
            public HardwareInputData Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInputData
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInputData
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInputData
        {
            public uint Message;
            public ushort ParameterLow;
            public ushort ParameterHigh;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint inputCount,
            Input[] inputs,
            int inputSize);
    }
}
