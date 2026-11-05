// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace LegionTweaks
{
    internal static class PawnIoTrust
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustFile { public uint Size; public IntPtr Path, Handle, Subject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint Size; public IntPtr Policy, Sip;
            public uint Ui, Revocation, Choice; public IntPtr File;
            public uint Action; public IntPtr State, Url;
            public uint Flags, Context; public IntPtr SignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

        internal static bool IsTrustedPublisher(string path)
        {
            IntPtr name = Marshal.StringToCoTaskMemUni(path);
            IntPtr file = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(TrustFile)));
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), Ui = 2,
                Choice = 1, File = file, Action = 1, Flags = 0x1000 }; // Cache-only; never prompt or fetch certificates.
            try {
                Marshal.StructureToPtr(new TrustFile { Size = (uint)Marshal.SizeOf(typeof(TrustFile)), Path = name }, file, false);
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
                using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    return string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), "namazso.eu", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally {
                data.Action = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                Marshal.FreeCoTaskMem(file); Marshal.FreeCoTaskMem(name);
            }
        }
    }
}
