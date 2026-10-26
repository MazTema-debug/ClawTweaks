// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks.Tray
{
    // Validate the registered CoreWindow, then locate its unique shell frame by
    // exact AppUserModelID and the system frame-host image. These are different
    // HWNDs on Windows 11; the CoreWindow itself need not report IsIconic.
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
            IntPtr frame = FindFrame(window, family);
            return frame != IntPtr.Zero && IsIconic(frame);
        }
        internal static bool IsRestored(IntPtr window, string family)
        {
            IntPtr frame = FindFrame(window, family);
            return frame != IntPtr.Zero && IsWindowVisible(frame) && !IsIconic(frame);
        }
        internal static Task<bool> HideAsync(IntPtr window, string family) => SetVisibility(window, family, false);
        internal static Task<bool> RestoreAsync(IntPtr window, string family) => SetVisibility(window, family, true);
        private static async Task<bool> SetVisibility(IntPtr window, string family, bool visible)
        {
            IntPtr frame = FindFrame(window, family);
            if (frame == IntPtr.Zero) return false;
            if (IsWindowVisible(frame) == visible && (!visible || !IsIconic(frame))) return true;
            if (!ShowWindowAsync(frame, visible ? 9 : 0)) return false; // SW_RESTORE / SW_HIDE, only our exact frame.
            for (int i = 0; i < 30; i++) {
                if (!IsWindow(frame)) return false;
                if (IsWindowVisible(frame) == visible && (!visible || !IsIconic(frame)))
                    return IsSystemFrameHost(frame) && ApplicationId(frame) == family + "!App";
                await Task.Delay(25).ConfigureAwait(false);
            }
            return false;
        }
        private static IntPtr FindFrame(IntPtr window, string family)
        {
            if (!IsOwned(window, family)) return IntPtr.Zero;
            string appId = family + "!App";
            var candidates = new List<DesktopFrameCandidate>();
            EnumWindows((frame, parameter) => {
                var name = new StringBuilder(256); GetClassNameW(frame, name, name.Capacity);
                if (name.ToString() != "ApplicationFrameWindow" || !IsSystemFrameHost(frame)) return true;
                if (ApplicationId(frame) != appId) return true;
                candidates.Add(new DesktopFrameCandidate { Handle = frame.ToInt64(), ClassName = name.ToString(), ApplicationId = appId, SystemFrameHost = true });
                return candidates.Count < 16;
            }, IntPtr.Zero);
            return new IntPtr(DesktopFrameSelection.Select(appId, candidates));
        }
        private static bool IsSystemFrameHost(IntPtr window)
        {
            uint pid; GetWindowThreadProcessId(window, out pid);
            using (var process = OpenProcess(0x1000, false, pid)) {
                if (process.IsInvalid) return false;
                uint size = 32768; var path = new StringBuilder((int)size);
                return QueryFullProcessImageNameW(process, 0, path, ref size)
                    && string.Equals(path.ToString(), Path.Combine(Environment.SystemDirectory, "ApplicationFrameHost.exe"), StringComparison.OrdinalIgnoreCase);
            }
        }
        private static string ApplicationId(IntPtr window)
        {
            IPropertyStore store = null;
            try {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(window, ref iid, out store) < 0 || store == null) return null;
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                PropertyValue value;
                if (store.GetValue(ref key, out value) < 0) return null;
                try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Text) : null; }
                finally { PropVariantClear(ref value); }
            }
            catch (Exception) { return null; }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Text;
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyValue value);
            [PreserveSig] int Commit();
        }
        private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetClassNameW(IntPtr window, StringBuilder name, int count);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint count);
        [DllImport("shell32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int PropVariantClear(ref PropertyValue value);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool ShowWindowAsync(IntPtr window, int command);
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
