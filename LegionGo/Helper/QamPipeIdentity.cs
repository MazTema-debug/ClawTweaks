// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    internal static class QamPipeIdentity
    {
        internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LegionTweaks-Z1E");
        internal static string QamImage => Path.Combine(InstallDirectory, "Qam", "LegionTweaks.QamProbe.exe");
        internal static string HelperImage => Path.Combine(InstallDirectory, "LegionTweaks.Helper.exe");
        internal static bool Matches(SafePipeHandle pipe, bool server, SecurityIdentifier user, string expectedImage, int? sessionId = null)
        {
            uint pid;
            if (!(server ? GetNamedPipeServerProcessId(pipe, out pid) : GetNamedPipeClientProcessId(pipe, out pid))) return false;
            if (sessionId.HasValue && (!ProcessIdToSessionId(pid, out uint session) || session != (uint)sessionId.Value)) return false;
            using (var process = OpenProcess(0x1000, false, pid)) {
                if (process.IsInvalid) return false;
                var image = new StringBuilder(32768); uint length = (uint)image.Capacity;
                if (!QueryFullProcessImageName(process, 0, image, ref length)
                    || !string.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase)
                    || !OpenProcessToken(process, 8, out IntPtr token)) return false;
                try { using (var identity = new WindowsIdentity(token)) return user.Equals(identity.User); }
                finally { CloseHandle(token); }
            }
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
