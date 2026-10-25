// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks.Tray
{
    // Inspect only the CoreWindow registered by our in-package app service.
    // No global hooks, window-title matching, cross-process writes or input.
    internal static class NativeDesktopWindow
    {
        internal static bool IsOwned(IntPtr window, string family)
        {
            if (window == IntPtr.Zero || string.IsNullOrEmpty(family) || !IsWindow(window)) return false;
            uint pid; GetWindowThreadProcessId(window, out pid);
            if (pid == 0) return false;
            using (var process = OpenProcess(0x1000, false, pid)) {
                if (process.IsInvalid) return false;
                uint size = 256; var name = new StringBuilder((int)size);
                return GetPackageFamilyName(process, ref size, name) == 0 && name.ToString() == family;
            }
        }
        internal static bool IsMinimized(IntPtr window, string family)
        {
            if (!IsOwned(window, family)) return false;
            IntPtr frame = GetAncestor(window, 2); // GA_ROOT: Windows may own the enclosing UWP frame.
            return frame != IntPtr.Zero && IsIconic(frame);
        }
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder name);
    }
}
