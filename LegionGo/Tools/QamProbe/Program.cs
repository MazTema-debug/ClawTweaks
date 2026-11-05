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
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try {
                bool keyboardOnly = args.Length == 1 && args[0] == "--solo-teclado";
                if (args.Length != 0 && !keyboardOnly) throw new ArgumentException("Opción disponible: --solo-teclado.");
                using (var instance = new Mutex(true, @"Local\LegionTweaks.QamProbe", out bool first)) {
                    if (!first) throw new InvalidOperationException("El panel de prueba ya está iniciado. Usa Ctrl+Mayús+F10 para mostrarlo.");
                    if (!keyboardOnly && !ReadIdentity().IsSupported)
                        throw new InvalidOperationException("La lectura de botones requiere una Legion Go original 83E1 / Z1 Extreme.");
                    string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegionTweaks", "QamProbe");
                    Directory.CreateDirectory(directory);
                    string logPath = Path.Combine(directory, "qam-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Process.GetCurrentProcess().Id + ".log");
                    using (var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true })
                    using (var form = new ProbeForm(keyboardOnly, log)) Application.Run(form);
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "QAM de prueba", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
