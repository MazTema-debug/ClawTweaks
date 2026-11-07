// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LegionTweaks
{
    internal sealed class QamRuntime : IQamRuntime
    {
        private readonly string family;
        private readonly QamInstanceClient client = new QamInstanceClient();
        public int SessionId { get; } = Process.GetCurrentProcess().SessionId;
        internal QamRuntime(string family) { this.family = family; }
        private QamInstanceResponse Read()
        {
            try { return client.Send(new QamInstanceRequest { Operation = "status" }, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { return null; }
            catch (TimeoutException) { return null; }
        }
        private static void RequireInstalled()
        {
            string directory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(directory, QamPipeIdentity.InstallDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("El acceso al menú necesita el helper instalado.");
            foreach (string path in new[] { directory, Path.GetDirectoryName(QamPipeIdentity.QamImage), QamPipeIdentity.QamImage, QamPipeIdentity.QamImage + ".config" })
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("La ruta del menú no puede ser un enlace.");
        }
        public QamInstanceResponse EnsureRunning()
        {
            if (SessionId <= 0) throw new InvalidOperationException("No se abre una interfaz en sesión 0.");
            RequireInstalled();
            var existing = Read();
            if (existing?.Success == true && existing.Running) return existing;
            QamUserSession.LaunchHidden(QamPipeIdentity.QamImage);
            var budget = Stopwatch.StartNew();
            while (budget.ElapsedMilliseconds < 6500) {
                var ready = Read();
                if (ready?.Success == true && ready.Running) return ready;
                Thread.Sleep(80);
            }
            throw new InvalidOperationException("El menú no confirmó su inicio. Cierra una instancia anterior si no corresponde a esta entrega.");
        }
        public QamInstanceResponse Command(string operation, DesktopViewRegistration view, int clientPid)
        {
            EnsureRunning();
            return client.Send(new QamInstanceRequest { Operation = operation, RequestId = Guid.NewGuid().ToString("N"),
                DesktopWindow = view?.Window ?? 0, DesktopProcessId = view == null ? 0 : clientPid, DesktopViewId = view?.ViewId },
                CancellationToken.None).GetAwaiter().GetResult(); // Never retry a toggle after an uncertain response.
        }
        public bool IsRunning(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited && process.SessionId == SessionId
                && string.Equals(process.MainModule.FileName, QamPipeIdentity.QamImage, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { return false; }
        }
        public bool IsDesktopWindow(long window, int pid) => QamUserSession.WindowBelongsToUser(window, pid, SessionId, family);
        public void RegisterDesktop(DesktopViewRegistration view, int pid) => QamUserSession.MarkDesktop(view, pid, SessionId, family);
    }
}
