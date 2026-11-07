// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LegionTweaks.QamProbe
{
    internal static class DesktopWindowControl
    {
        internal static string Toggle(QamInstanceRequest request)
        {
            if (request?.Valid != true || request.Operation != "desktop_toggle") throw new ArgumentException("Acción de escritorio inválida.");
            if (!ProbeNative.InteractiveDesktop()) throw new InvalidOperationException("La app no se abre ni minimiza sobre bloqueo o UAC.");
            if (!string.Equals(Process.GetCurrentProcess().MainModule.FileName, QamPipeIdentity.QamImage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La acción de escritorio requiere el menú instalado.");
            string family = File.ReadAllText(Path.Combine(QamPipeIdentity.InstallDirectory, "allowed-family.txt")).Trim();
            if (!family.StartsWith("MazTema.LegionTweaks_", StringComparison.Ordinal) || family.Length > 256 || family.IndexOfAny(new[] { '!', '/', '\\', '"' }) >= 0)
                throw new InvalidDataException("La identidad de la app instalada no es válida.");
            if (request.DesktopWindow != 0 && QamUserSession.MatchesDesktopMarker(new DesktopViewRegistration { Window = request.DesktopWindow, ViewId = request.DesktopViewId })
                && QamUserSession.WindowBelongsToUser(request.DesktopWindow, request.DesktopProcessId,
                Process.GetCurrentProcess().SessionId, family)) {
                IntPtr core = new IntPtr(request.DesktopWindow), root = GetAncestor(core, 2);
                if (root == IntPtr.Zero) root = core;
                // The helper registers only a desktop view; the Game Bar view never registers here.
                IntPtr foreground = GetForegroundWindow();
                IntPtr foregroundRoot = GetAncestor(foreground, 2);
                if (!IsIconic(root) && IsWindowVisible(root) && (root == foreground || root == foregroundRoot)) {
                    if (!ShowWindowAsync(root, 6)) throw new InvalidOperationException("No se pudo solicitar la minimización de la app.");
                    return "Minimización de la app de escritorio solicitada.";
                }
            }
            object manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
            try {
                int result = ((IApplicationActivationManager)manager).ActivateApplication(family + "!App", "", 0, out uint pid);
                Marshal.ThrowExceptionForHR(result);
                return "Apertura de la app de escritorio solicitada; la app reutiliza su vista existente.";
            }
            finally { Marshal.FinalReleaseComObject(manager); }
        }
        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string app, [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint pid);
        }
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    }
}
