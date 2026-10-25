// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    // Positive evidence only: a live Microsoft Game Bar host owns the foreground
    // window on the ordinary desktop. No activation, input, process termination,
    // device API or application-specific exception is performed here.
    internal sealed class NativeGameBarForeground : IGameBarForeground
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly MouseOutput desktop = new MouseOutput();
        private long checkedAt = -1000;
        private bool foreground;
        public bool IsForeground()
        {
            long now = clock.ElapsedMilliseconds;
            if (now - checkedAt < 100) return foreground;
            checkedAt = now; foreground = false;
            try {
                var current = desktop.Inspect();
                if (!current.Available) return false;
                uint pid; GetWindowThreadProcessId(new IntPtr(current.Foreground), out pid);
                if (pid == 0) return false;
                foreground = IsGameBarHostProcess(pid);
            } catch (Exception) { foreground = false; }
            return foreground;
        }
        internal static bool IsGameBarHostProcess(uint pid)
        {
            try {
                using (var process = OpenProcess(0x1000, false, pid)) {
                    if (process.IsInvalid) return false;
                    var family = new StringBuilder(256); uint familySize = 256;
                    if (GetPackageFamilyName(process, ref familySize, family) != 0
                        || family.ToString() != "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe") return false;
                    var path = new StringBuilder(32768); uint pathSize = (uint)path.Capacity;
                    if (!QueryFullProcessImageName(process, 0, path, ref pathSize)) return false;
                    string name = Path.GetFileName(path.ToString());
                    return string.Equals(name, "GameBar.exe", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "GameBarFTServer.exe", StringComparison.OrdinalIgnoreCase);
                }
            } catch (Exception) { return false; }
        }
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    }
}
