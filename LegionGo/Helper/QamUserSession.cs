// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    internal static class QamUserSession
    {
        internal static bool MatchesDesktopMarker(DesktopViewRegistration view) => view?.Valid == true
            && GetProp(new IntPtr(view.Window), "LegionTweaks.Desktop." + view.ViewId) == new IntPtr(1);
        internal static void MarkDesktop(DesktopViewRegistration view, int pid, int session, string family)
        {
            if (view?.Valid != true || !WindowBelongsToUser(view.Window, pid, session, family)
                || !SetProp(new IntPtr(view.Window), "LegionTweaks.Desktop." + view.ViewId, new IntPtr(1)))
                throw new InvalidOperationException("No se pudo registrar la vista de escritorio.");
        }
        internal static bool WindowBelongsToUser(long window, int pid, int session, string family)
        {
            if (window <= 0 || pid <= 0 || !IsWindow(new IntPtr(window))) return false;
            GetWindowThreadProcessId(new IntPtr(window), out uint actual);
            if (actual != (uint)pid || !ProcessIdToSessionId(actual, out uint found) || found != session) return false;
            using (var process = OpenProcess(0x1000, false, actual)) {
                if (process.IsInvalid || !OpenProcessToken(process, 8, out IntPtr token)) return false;
                try {
                    using (var identity = new WindowsIdentity(token)) if (!WindowsIdentity.GetCurrent().User.Equals(identity.User)) return false;
                }
                finally { CloseHandle(token); }
                uint size = 0;
                if (GetPackageFamilyName(process, ref size, null) != 122 || size > 256) return false;
                var name = new StringBuilder((int)size);
                return GetPackageFamilyName(process, ref size, name) == 0 && name.ToString() == family;
            }
        }
        internal static int LaunchHidden(string image, bool keyboardOnly = false)
        {
            int session = Process.GetCurrentProcess().SessionId;
            if (session <= 0) throw new InvalidOperationException("El menú necesita la sesión interactiva del usuario; no se inicia en sesión 0.");
            string arguments = "--start-hidden" + (keyboardOnly ? " --solo-teclado" : "");
            var current = Process.GetCurrentProcess();
            using (var self = OpenProcess(0x1000, false, (uint)current.Id)) {
                if (!OpenProcessToken(self, 8, out IntPtr ownToken)) throw new Win32Exception();
                try {
                    int elevated;
                    if (!GetTokenInformation(ownToken, 20, out elevated, sizeof(int), out int _)) throw new Win32Exception();
                    if (elevated == 0) {
                        // Already a regular user process in the intended session; never request elevation.
                        using (var child = Process.Start(new ProcessStartInfo(image, arguments) {
                            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(image), CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })) {
                            if (child == null) throw new InvalidOperationException("No se pudo iniciar el menú rápido.");
                            return child.Id;
                        }
                    }
                }
                finally { CloseHandle(ownToken); }
            }
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) throw new InvalidOperationException("El escritorio del usuario todavía no está listo.");
            GetWindowThreadProcessId(shell, out uint shellPid);
            if (!ProcessIdToSessionId(shellPid, out uint shellSession) || shellSession != session)
                throw new InvalidOperationException("El escritorio pertenece a otra sesión.");
            using (var process = OpenProcess(0x1000, false, shellPid)) {
                if (process.IsInvalid || !OpenProcessToken(process, 0x000a, out IntPtr token)) throw new Win32Exception();
                try {
                    using (var identity = new WindowsIdentity(token)) if (!WindowsIdentity.GetCurrent().User.Equals(identity.User))
                        throw new InvalidOperationException("El escritorio pertenece a otro usuario.");
                    if (!DuplicateTokenEx(token, 0x000b, IntPtr.Zero, 2, 1, out IntPtr primary)) throw new Win32Exception();
                    try {
                        if (!CreateEnvironmentBlock(out IntPtr environment, primary, false)) throw new Win32Exception();
                        try {
                            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = @"winsta0\default", Flags = 1, ShowWindow = 0 };
                            var command = new StringBuilder("\"" + image + "\" " + arguments);
                            // Prefer a process outside the scheduled helper's job, so stopping the task does not own the QAM lifetime.
                            bool created = CreateProcessWithTokenW(primary, 0, image, command, 0x09000400, environment, Path.GetDirectoryName(image), ref startup, out ProcessInformation child);
                            if (!created && Marshal.GetLastWin32Error() == 5) {
                                // A host job can forbid breakaway. Keep the user/session boundary; the next helper startup can re-create a terminated QAM.
                                command = new StringBuilder("\"" + image + "\" " + arguments);
                                created = CreateProcessWithTokenW(primary, 0, image, command, 0x08000400, environment, Path.GetDirectoryName(image), ref startup, out child);
                            }
                            if (!created) throw new Win32Exception();
                            CloseHandle(child.Thread); CloseHandle(child.Process);
                            return (int)child.Pid;
                        }
                        finally { DestroyEnvironmentBlock(environment); }
                    }
                    finally { CloseHandle(primary); }
                }
                finally { CloseHandle(token); }
            }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
        {
            internal int Size; internal string Reserved, Desktop, Title;
            internal int X, Y, Width, Height, XChars, YChars, Fill, Flags;
            internal short ShowWindow, ReservedSize; internal IntPtr ReservedPointer, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint Pid, Tid; }
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint size, StringBuilder family);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int information, out int value, int size, out int returned);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr security, int level, int type, out IntPtr primary);
        [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(IntPtr token, uint logon, string image,
            StringBuilder command, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
