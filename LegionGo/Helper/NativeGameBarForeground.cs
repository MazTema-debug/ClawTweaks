// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    // Called by the controller worker. Positive evidence on the ordinary desktop:
    // Windows redirects input to Game Bar, or a verified Game Bar host owns focus.
    // Compact Mode can redirect input without a Game Bar foreground HWND or widget.
    // No activation, synthetic input, process/window writes or device API here.
    internal sealed class NativeGameBarForeground : IGameBarForeground
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Func<long> milliseconds;
        private readonly Func<MouseDesktop> inspectDesktop;
        private readonly Func<bool> inputRedirected;
        private readonly Func<long, bool> nativeHost;
        private long checkedAt = -1000;
        private bool foreground;
        internal NativeGameBarForeground() : this(null, null, null, null) { }
        // Test injection never opens a desktop or Windows Runtime factory.
        internal NativeGameBarForeground(Func<long> milliseconds, Func<MouseDesktop> inspectDesktop,
            Func<bool> inputRedirected, Func<long, bool> nativeHost)
        {
            this.milliseconds = milliseconds ?? (() => clock.ElapsedMilliseconds);
            this.inspectDesktop = inspectDesktop ?? new MouseOutput().Inspect;
            this.inputRedirected = inputRedirected ?? new WindowsInputRedirection().Read;
            this.nativeHost = nativeHost ?? IsNativeHost;
        }
        public bool IsForeground()
        {
            long now = milliseconds();
            if (now >= checkedAt && now - checkedAt < 100) return foreground;
            checkedAt = now; foreground = false;
            try {
                var current = inspectDesktop();
                if (!current.Available) return false;
                // Never retain a true API result after failure. Preserve the
                // existing verified-host fallback when WinRT is unavailable.
                try { foreground = inputRedirected(); } catch (Exception) { }
                if (!foreground) foreground = nativeHost(current.Foreground);
            } catch (Exception) { foreground = false; }
            return foreground;
        }
        private static bool IsNativeHost(long window)
        {
            uint pid; GetWindowThreadProcessId(new IntPtr(window), out pid);
            return pid != 0 && IsGameBarHostProcess(pid);
        }
        private sealed class WindowsInputRedirection
        {
            private MethodInfo getter;
            internal bool Read()
            {
                // .NET Framework provides the Windows Runtime projection. Resolve
                // only this fixed public OS property, without an SDK dependency or
                // an assembly loaded from an application/user-provided file.
                if (getter == null) {
                    var type = Type.GetType("Windows.Gaming.UI.GameBar, Windows, ContentType=WindowsRuntime", true);
                    getter = type.GetProperty("IsInputRedirected", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                    if (getter == null || getter.ReturnType != typeof(bool)) throw new MissingMemberException("GameBar.IsInputRedirected");
                }
                // Cache metadata, never the property value across fresh samples.
                return (bool)getter.Invoke(null, null);
            }
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
