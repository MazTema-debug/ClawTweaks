// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using System.Windows.Forms;

namespace LegionTweaks.QamProbe
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool hidden = Array.IndexOf(args, "--start-hidden") >= 0;
            try {
                bool keyboardOnly = Array.IndexOf(args, "--solo-teclado") >= 0;
                bool open = Array.IndexOf(args, "--open") >= 0;
                if ((hidden && open) || args.Length != (hidden ? 1 : 0) + (keyboardOnly ? 1 : 0) + (open ? 1 : 0))
                    throw new ArgumentException("Opciones: --start-hidden, --open y --solo-teclado.");
                using (var instance = new Mutex(false, @"Local\LegionTweaks.QamProbe")) {
                    bool owner;
                    try { owner = instance.WaitOne(0); } catch (AbandonedMutexException) { owner = true; }
                    if (!owner) {
                        var client = new QamInstanceClient(Process.GetCurrentProcess().MainModule.FileName);
                        var request = new QamInstanceRequest { Operation = hidden ? "status" : "open", RequestId = hidden ? null : Guid.NewGuid().ToString("N") };
                        // A second invocation only talks to the owner; it never starts a reader or creates a form.
                        var wait = Stopwatch.StartNew(); Exception failure = null;
                        do {
                            try {
                                var result = client.Send(request, CancellationToken.None).GetAwaiter().GetResult();
                                if (!result.Success) throw new InvalidOperationException(result.Message);
                                return 0;
                            }
                            catch (Exception ex) when (ex is IOException || ex is TimeoutException) { failure = ex; Thread.Sleep(100); }
                        } while (wait.ElapsedMilliseconds < 5000);
                        throw new InvalidOperationException("Hay otra instancia sin el canal actualizado; ciérrala antes de volver a abrir el menú.", failure);
                    }
                    try {
                    if (!keyboardOnly && !ReadIdentity().IsSupported)
                        throw new InvalidOperationException("La lectura de botones requiere una Legion Go original 83E1 / Z1 Extreme.");
                    string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegionTweaks", "QamProbe");
                    Directory.CreateDirectory(directory);
                    string logPath = Path.Combine(directory, "qam-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Process.GetCurrentProcess().Id + ".log");
                    using (var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true })
                    using (var form = new ProbeForm(keyboardOnly, log))
                    using (var context = new ApplicationContext()) {
                        // Do not assign MainForm: Run(Form) would show it during an automatic hidden start.
                        form.FormClosed += (sender, eventArgs) => context.ExitThread();
                        form.StartHidden(string.Equals(Process.GetCurrentProcess().MainModule.FileName, QamPipeIdentity.QamImage, StringComparison.OrdinalIgnoreCase));
                        using (var server = new QamInstanceServer(form.ProcessInstanceRequest)) {
                            if (!hidden) form.ShowPanel("inicio manual");
                            Application.Run(context);
                        }
                    }
                    }
                    finally { instance.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception ex) {
                if (!hidden) MessageBox.Show(ex.Message, "Menú rápido", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static DeviceIdentity ReadIdentity()
        {
            var identity = new DeviceIdentity();
            using (var search = new ManagementObjectSearcher("SELECT Vendor, Name, Version FROM Win32_ComputerSystemProduct"))
            using (var results = search.Get()) foreach (ManagementObject item in results)
                using (item) {
                    identity.Manufacturer = item["Vendor"] as string;
                    identity.Model = item["Name"] as string;
                    identity.Version = item["Version"] as string;
                }
            using (var search = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
            using (var results = search.Get()) foreach (ManagementObject item in results)
                using (item) identity.Cpu = item["Name"] as string;
            return identity;
        }
    }
}
