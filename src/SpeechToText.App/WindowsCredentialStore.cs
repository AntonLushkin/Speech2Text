using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SpeechToText.Core;

namespace SpeechToText.App
{
    public sealed class WindowsCredentialStore : ICredentialStore
    {
        private const int CredentialTypeGeneric = 1;
        private const int PersistLocalMachine = 2;

        public string Read(string name)
        {
            IntPtr pointer;
            if (!CredRead(name, CredentialTypeGeneric, 0, out pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1168)
                {
                    return string.Empty;
                }
                throw new Win32Exception(error);
            }

            try
            {
                var credential = (NativeCredential)Marshal.PtrToStructure(
                    pointer,
                    typeof(NativeCredential));
                if (credential.CredentialBlob == IntPtr.Zero ||
                    credential.CredentialBlobSize == 0)
                {
                    return string.Empty;
                }

                return Marshal.PtrToStringUni(
                    credential.CredentialBlob,
                    (int)credential.CredentialBlobSize / 2);
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public void Write(string name, string secret)
        {
            if (string.IsNullOrEmpty(secret))
            {
                Delete(name);
                return;
            }

            var bytes = Encoding.Unicode.GetBytes(secret);
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new NativeCredential
                {
                    Type = CredentialTypeGeneric,
                    TargetName = name,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = Environment.UserName
                };

                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(blob);
            }
        }

        public void Delete(string name)
        {
            if (!CredDelete(name, CredentialTypeGeneric, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1168)
                {
                    throw new Win32Exception(error);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(
            string target,
            int type,
            int flags,
            out IntPtr credential);

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(
            ref NativeCredential credential,
            uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(
            string target,
            int type,
            int flags);

        [DllImport("Advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}
