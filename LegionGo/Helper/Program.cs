// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    internal sealed class FileJournal : IJournal
    {
        private readonly string path;
        public FileJournal(string path) { this.path = path; }
        public IList<OriginalValue> Load()
        {
            if (!File.Exists(path)) return new List<OriginalValue>();
            // Fail closed if damaged: never replace a backup with guessed/default values.
            return Protocol.Decode<List<OriginalValue>>(File.ReadAllText(path));
        }
        public void Save(IList<OriginalValue> values)
        {
            string temporary = path + ".tmp";
            byte[] data = Encoding.UTF8.GetBytes(Protocol.Encode(values.ToList()));
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(data, 0, data.Length); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }

    internal static class Program
    {
        private static readonly SecurityIdentifier User = WindowsIdentity.GetCurrent().User;
        private static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionTweaks", User.Value);
        private static int Main(string[] args)
        {
            try
            {
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("Ejecuta el servicio como administrador.");
                if (args.SequenceEqual(new[] { "--controller-slot-probe" }))
                {
                    Console.OutputEncoding = Encoding.UTF8;
                    return XInputSlotProbe.RunWorker();
                }
                if (args.SequenceEqual(new[] { "--controller-mode-query" }))
                    return ControllerModeRecovery.QueryWorker(DataDirectory);
                if (args.Length == 3 && args[0] == "--controller-mode-watch")
                    return ControllerModeRecovery.Watch(DataDirectory, args[1], args[2]);
                if (args.Length == 3 && args[0] == "--controller-mode-worker")
                    return ControllerModeRecovery.Worker(DataDirectory, args[1], args[2]);
                if (args.Length == 3 && args[0] == "--controller-device-watch")
                    return PhysicalControllerRecovery.Watch(DataDirectory, args[1], args[2]);
                if (args.Length == 3 && args[0] == "--controller-device-worker")
                {
                    if (args[1] == "disable")
                    {
                        using (var gateHardware = new LenovoHardware())
                            if (!gateHardware.Identity.IsSupported) throw new InvalidOperationException("Modelo no compatible con la reconexión del mando.");
                    }
                    return PhysicalControllerRecovery.Worker(DataDirectory, args[1], args[2]);
                }
                if (args.Length == 4 && args[0] == "--controller-watchdog")
                    return HidHideLease.Watch(DataDirectory, int.Parse(args[1]), long.Parse(args[2]), args[3]);
                using (var hardware = new LenovoHardware())
                {
                    // This path is deliberately read-only and works on unsupported PCs too.
                    if (args.SequenceEqual(new[] { "--diagnose" }))
                    {
                        var diagnostic = new ControlEngine(hardware, new ReadOnlyJournal());
                        Console.WriteLine(Protocol.Encode(diagnostic.Execute(new Request { Operation = "status" })));
                        return 0;
                    }
                    if (!hardware.Identity.IsSupported)
                        throw new InvalidOperationException("Solo Legion Go 8APU1 / Z1 Extreme. Usa --diagnose para ver la detección.");
                    if (args.Length > 0 && !args.SequenceEqual(new[] { "--restore" }) && !args.SequenceEqual(new[] { "--recover-controller" })
                        && !args.SequenceEqual(new[] { "--test-controller-mode-cycle" }) && !args.SequenceEqual(new[] { "--suppress-controller-startup-once" }))
                        throw new ArgumentException("Opciones: --diagnose, --restore, --recover-controller, --test-controller-mode-cycle, --suppress-controller-startup-once, o sin argumentos para iniciar.");
                    bool created;
                    using (var mutex = new Mutex(true, "Global\\LegionTweaks.Z1E." + User.Value, out created))
                    {
                        if (!created) throw new InvalidOperationException("El servicio ya está activo. Detén la tarea LegionTweaks-Z1E antes de restaurar por consola.");
                        SecureDirectory();
                        Exception recoveryFailure = null;
                        try { ControllerModeRecovery.RecoverPending(DataDirectory); }
                        catch (Exception ex) { recoveryFailure = ex; }
                        try { PhysicalControllerRecovery.RecoverPending(DataDirectory); }
                        catch (Exception ex) { recoveryFailure = recoveryFailure == null ? ex : new AggregateException(recoveryFailure, ex); }
                        try { new HidHideLease(DataDirectory).Restore(); }
                        catch (Exception ex) { recoveryFailure = recoveryFailure == null ? ex : new AggregateException(recoveryFailure, ex); }
                        if (recoveryFailure != null) throw new InvalidOperationException("Hay una recuperación del mando pendiente.", recoveryFailure);
                        if (args.SequenceEqual(new[] { "--recover-controller" })) { Console.WriteLine("Recuperación del mando físico completada."); return 0; }
                        if (args.SequenceEqual(new[] { "--suppress-controller-startup-once" })) { new ControllerStartupStore(DataDirectory).SuppressNextStartup(); return 0; }
                        if (args.SequenceEqual(new[] { "--test-controller-mode-cycle" })) { ControllerModeRecovery.TestCycle(DataDirectory); return 0; }
                        using (var shortcuts = args.Length == 0 ? new LegionShortcutService(DataDirectory) : null)
                        using (var controller = new ControllerService(DataDirectory, shortcuts))
                        {
                        var engine = new ControlEngine(hardware, new FileJournal(Path.Combine(DataDirectory, "originals.json")), controller);
                        if (args.Length > 0)
                        {
                            var result = engine.Execute(new Request { Operation = "restore" });
                            Console.WriteLine(Protocol.Encode(result));
                            return result.Success ? 0 : 1;
                        }
                        string allowedFamily = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "allowed-family.txt")).Trim();
                        if (!allowedFamily.StartsWith("MazTema.LegionTweaks_", StringComparison.Ordinal))
                            throw new InvalidOperationException("Instalación incompleta: identidad del widget inválida.");
                        var console = GetConsoleWindow();
                        if (console != IntPtr.Zero) ShowWindow(console, 0);
                        var startup = new ControllerStartup(new ControllerStartupStore(DataDirectory), controller.PreparePlayerOneMode,
                            controller.GetStatus, () => {
                                try { PlayerOnePort.EnsureApplicationsClosed(); return null; }
                                catch (InvalidOperationException ex) { return ex.Message; }
                            }, hardware.Identity.IsSupported);
                        controller.StateChanged += startup.Observe;
                        using (var session = new HelperSession(engine, Path.Combine(DataDirectory, "Recordings"), controller: controller, shortcuts: shortcuts,
                            legionSpace: new LegionSpaceControl(new LegionSpaceBackend(), new LegionSpaceJournal(DataDirectory), hardware.Identity.IsSupported),
                            diagnostics: new DiagnosticsProvider(DataDirectory, controller).GetReport,
                            rtss: new RtssOverlayService(() => engine.Execute(new Request { Operation = "telemetry" }),
                                new RtssMemoryTransport(), hardware.Identity.IsSupported), startup: startup))
                        {
                            EventHandler onExit = (s, e) => session.Dispose();
                            Microsoft.Win32.PowerModeChangedEventHandler onPower = (s, e) => {
                                if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
                                {
                                    session.Suspend();
                                    shortcuts.Suspend(true);
                                    engine.Execute(new Request { Operation = "controller_stop" });
                                }
                                else if (e.Mode == Microsoft.Win32.PowerModes.Resume) shortcuts.Suspend(false);
                            };
                            AppDomain.CurrentDomain.ProcessExit += onExit;
                            Microsoft.Win32.SystemEvents.PowerModeChanged += onPower;
                            try { startup.RunAtStartup(); Serve(session, allowedFamily).GetAwaiter().GetResult(); }
                            finally { controller.StateChanged -= startup.Observe; AppDomain.CurrentDomain.ProcessExit -= onExit; Microsoft.Win32.SystemEvents.PowerModeChanged -= onPower; }
                        }
                        }
                    }
                    return 0;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        private static void SecureDirectory()
        {
            Directory.CreateDirectory(DataDirectory);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(DataDirectory).SetAccessControl(acl);
            // Traverse to the readable CSV folder without exposing the protected originals journal.
            acl.AddAccessRule(new FileSystemAccessRule(User, FileSystemRights.Traverse, AccessControlType.Allow));
            new DirectoryInfo(DataDirectory).SetAccessControl(acl);
            string recordings = Path.Combine(DataDirectory, "Recordings");
            if (Directory.Exists(recordings) && (File.GetAttributes(recordings) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("La carpeta de registros no puede ser un enlace.");
            Directory.CreateDirectory(recordings);
            var recordingAcl = new DirectorySecurity();
            recordingAcl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                recordingAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            recordingAcl.AddAccessRule(new FileSystemAccessRule(User, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            // The owner can remove finished CSVs, but cannot create/replace paths used by the elevated writer.
            recordingAcl.AddAccessRule(new FileSystemAccessRule(User, FileSystemRights.Delete,
                InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            new DirectoryInfo(recordings).SetAccessControl(recordingAcl);
        }
        private static async Task Serve(HelperSession session, string allowedFamily)
        {
            var acl = new PipeSecurity();
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            acl.AddAccessRule(new PipeAccessRule(User, PipeAccessRights.FullControl, AccessControlType.Allow));
            // UWP token needs this ACE; actual peer SID AND package identity are checked below.
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-15-2-1"), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            while (true)
            {
                using (var pipe = new NamedPipeServerStream(Protocol.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, acl))
                {
                    await pipe.WaitForConnectionAsync();
                    try
                    {
                        if (!IsAllowedClient(pipe, allowedFamily)) continue;
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        {
                            var read = ReadBoundedLine(reader);
                            if (await Task.WhenAny(read, Task.Delay(5000)) != read) continue;
                            var request = Protocol.Decode<Request>(await read);
                            var response = session.Execute(request);
                            await writer.WriteLineAsync(Protocol.Encode(response));
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(Path.Combine(DataDirectory, "helper.log"), DateTime.UtcNow.ToString("O") + " " + ex.Message + Environment.NewLine);
                    }
                }
            }
        }
        private static async Task<string> ReadBoundedLine(StreamReader reader)
        {
            var result = new StringBuilder();
            var buffer = new char[1];
            while (result.Length <= Protocol.MaxMessageLength)
            {
                if (await reader.ReadAsync(buffer, 0, 1) == 0) throw new EndOfStreamException();
                if (buffer[0] == '\n') return result.ToString();
                result.Append(buffer[0]);
            }
            throw new InvalidDataException("Message too long.");
        }
        private static bool IsAllowedClient(NamedPipeServerStream pipe, string allowedFamily)
        {
            uint pid;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)) return false;
            using (var process = OpenProcess(0x1000, false, pid))
            {
                if (process.IsInvalid) return false;
                uint size = 0;
                if (GetPackageFamilyName(process, ref size, null) != 122 || size > 256) return false;
                var family = new StringBuilder((int)size);
                if (GetPackageFamilyName(process, ref size, family) != 0 || family.ToString() != allowedFamily) return false;
                IntPtr token;
                if (!OpenProcessToken(process, 8, out token)) return false;
                try { using (var identity = new WindowsIdentity(token)) return User.Equals(identity.User); }
                finally { CloseHandle(token); }
            }
        }
        private sealed class ReadOnlyJournal : IJournal
        {
            public IList<OriginalValue> Load() => new List<OriginalValue>();
            public void Save(IList<OriginalValue> values) => throw new InvalidOperationException("Read-only diagnostics.");
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder family);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
