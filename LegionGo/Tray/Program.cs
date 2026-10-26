// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace LegionTweaks.Tray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            string sid = WindowsIdentity.GetCurrent().User.Value;
            bool created;
            using (var singleton = new Mutex(true, @"Local\LegionTweaks.Tray." + sid + "." + Process.GetCurrentProcess().SessionId, out created)) {
                if (!created) return;
                try {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var context = new TrayContext()) Application.Run(context);
                }
                catch (Exception) { /* Desktop retains its normal close behavior until the ready handshake. */ }
                finally { singleton.ReleaseMutex(); }
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Control dispatcher = new Control();
        private readonly NotifyIcon icon;
        private readonly Icon picture;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
        private AppServiceConnection connection;
        private IntPtr desktopWindow;
        private string family;
        private bool busy, closing, minimized, interfaceExiting;

        internal TrayContext()
        {
            var handle = dispatcher.Handle;
            picture = LoadIcon();
            menu.Items.Add("Abrir LegionTweaks", null, async (s, e) => await Command("Open"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Salir de la interfaz", null, async (s, e) => await Command("Exit"));
            icon = new NotifyIcon { Icon = picture, Text = "LegionTweaks Lab", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += async (s, e) => { if (e.Button == MouseButtons.Left) await Command("Open"); };
            timer.Tick += async (s, e) => {
                if (busy || closing || desktopWindow == IntPtr.Zero) return;
                bool now = NativeDesktopWindow.IsMinimized(desktopWindow, family);
                if (now && !minimized) { minimized = true; await Command("Minimized"); }
                else if (!now) minimized = false;
            };
            dispatcher.BeginInvoke(new Action(async () => await Connect()));
        }
        private async Task Connect()
        {
            try {
                family = Package.Current.Id.FamilyName;
                connection = new AppServiceConnection { AppServiceName = DesktopTrayProtocol.ServiceName, PackageFamilyName = family };
                connection.RequestReceived += WindowRequest;
                connection.ServiceClosed += (s, e) => {
                    try { if (!closing && !dispatcher.IsDisposed) dispatcher.BeginInvoke(new Action(ExitThread)); }
                    catch (InvalidOperationException) { /* The tray is already leaving its message loop. */ }
                };
                if (await connection.OpenAsync() != AppServiceConnectionStatus.Success) { ExitThread(); return; }
                var response = await connection.SendMessageAsync(new ValueSet { ["Command"] = "Ready", ["Version"] = DesktopTrayProtocol.Version });
                object value;
                if (response.Status != AppServiceResponseStatus.Success || !response.Message.TryGetValue("Window", out value)
                    || !(value is long) || !NativeDesktopWindow.IsOwned(new IntPtr((long)value), family)) { ExitThread(); return; }
                desktopWindow = new IntPtr((long)value);
                timer.Start();
            }
            catch (Exception) { ExitThread(); }
        }
        private async void WindowRequest(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try {
                object version, command = null;
                bool valid = !closing && desktopWindow != IntPtr.Zero
                    && args.Request.Message.TryGetValue("Version", out version) && version is int && (int)version == DesktopTrayProtocol.Version
                    && args.Request.Message.TryGetValue("Command", out command) && command is string;
                bool success = false;
                if (valid) success = await OnTray(async () => {
                    if (closing || desktopWindow == IntPtr.Zero) return false;
                    if ((string)command == "HideDesktop") return await NativeDesktopWindow.HideAsync(desktopWindow, family);
                    if ((string)command == "RestoreDesktop") return await NativeDesktopWindow.RestoreAsync(desktopWindow, family);
                    if ((string)command == "DesktopVisible") return NativeDesktopWindow.IsRestored(desktopWindow, family);
                    return false;
                });
                await args.Request.SendResponseAsync(new ValueSet { ["Success"] = success });
            }
            catch (Exception) { /* A broken connection uses the existing desktop recovery path. */ }
            finally { deferral.Complete(); }
        }
        private Task<bool> OnTray(Func<Task<bool>> operation)
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try {
                dispatcher.BeginInvoke(new Action(async () => {
                    try { result.TrySetResult(!closing && await operation()); }
                    catch (Exception) { result.TrySetResult(false); }
                }));
            }
            catch (InvalidOperationException) { result.TrySetResult(false); }
            return result.Task;
        }
        private async Task Command(string command)
        {
            if (busy || closing || desktopWindow == IntPtr.Zero || connection == null) return;
            // Command("Open") is called only by the tray icon/menu gesture. A
            // delayed reply must not steal focus after the user clicks elsewhere.
            var gesture = command == "Open" ? NativeDesktopWindow.CaptureOpenGesture() : null;
            busy = true;
            try {
                var response = await connection.SendMessageAsync(new ValueSet { ["Command"] = command, ["Version"] = DesktopTrayProtocol.Version });
                if (response.Status != AppServiceResponseStatus.Success) ExitThread();
                else {
                    object success;
                    if (!response.Message.TryGetValue("Success", out success) || !(success is bool) || !(bool)success)
                        icon.ShowBalloonTip(4000, "LegionTweaks", "No se completó la acción. La interfaz conserva su estado; puedes volver a intentarlo.", ToolTipIcon.Info);
                    else if (command == "Open") {
                        minimized = false;
                        if (NativeDesktopWindow.IsOpenGestureCurrent(gesture)
                            && !await NativeDesktopWindow.TryActivateAsync(desktopWindow, family, gesture)
                            && NativeDesktopWindow.IsOpenGestureCurrent(gesture))
                            icon.ShowBalloonTip(4000, "LegionTweaks", "La ventana está abierta. Selecciónala en la barra de tareas para llevarla al frente.", ToolTipIcon.Info);
                    }
                    else if (command == "Exit") interfaceExiting = true;
                }
            }
            catch (Exception) { ExitThread(); }
            finally { busy = false; }
        }
        protected override void ExitThreadCore()
        {
            if (closing) return;
            // Best effort before a failed connection removes the icon. The UWP
            // side also attempts ordinary view restoration on this disconnect.
            if (!interfaceExiting && desktopWindow != IntPtr.Zero) {
                try { NativeDesktopWindow.RecoverBeforeExit(desktopWindow, family); }
                catch (Exception) { }
            }
            closing = true; timer.Stop(); icon.Visible = false;
            connection?.Dispose(); connection = null;
            base.ExitThreadCore();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); icon.Dispose(); picture.Dispose(); menu.Dispose(); dispatcher.Dispose(); connection?.Dispose(); }
            base.Dispose(disposing);
        }
        private static Icon LoadIcon()
        {
            try {
                using (var bitmap = new Bitmap(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Assets", "Square44x44Logo.png"))) {
                    IntPtr handle = bitmap.GetHicon();
                    try { return (Icon)Icon.FromHandle(handle).Clone(); }
                    finally { DestroyIcon(handle); }
                }
            } catch (Exception) { return (Icon)SystemIcons.Application.Clone(); }
        }
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool DestroyIcon(IntPtr icon);
    }
}
