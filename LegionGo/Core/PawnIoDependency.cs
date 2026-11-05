// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace LegionTweaks
{
    [DataContract]
    public sealed class PawnIoStatus
    {
        [DataMember] public bool Supported { get; set; }
        [DataMember] public bool Installed { get; set; }
        [DataMember] public bool Usable { get; set; }
        [DataMember] public bool DriverRunning { get; set; }
        [DataMember] public bool CanInstall { get; set; }
        [DataMember] public bool Busy { get; set; }
        [DataMember] public string Version { get; set; }
        [DataMember] public string InstallState { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string CapturedAtUtc { get; set; }
        public PawnIoStatus Copy() => (PawnIoStatus)MemberwiseClone();
    }

    public interface IPawnIoPlatform
    {
        PawnIoStatus Inspect();
        // Runs only on a worker after an explicit installation request.
        // Returns after the interactive vendor installer exits; exit code alone
        // must never be used as evidence that the dependency is installed.
        void Install(Action<string> phase);
    }

    public interface IPawnIoDependency
    {
        PawnIoStatus GetStatus();
        PawnIoStatus Refresh();
        PawnIoStatus BeginInstall();
    }

    public static class PawnIoPackage
    {
        public const string Website = "https://pawnio.eu/";
        public const string InstallerUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
        public const string InstallerSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";
        public const int InstallerBytes = 3410960;
        public static readonly Version MinimumVersion = new Version(2, 2, 0);
        public static bool IsAcceptedInstaller(long bytes, string sha256, bool trustedPublisher) =>
            bytes == InstallerBytes && trustedPublisher && string.Equals(sha256, InstallerSha256, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class PawnIoDependency : IPawnIoDependency
    {
        private readonly object gate = new object();
        private readonly bool supported;
        private readonly IPawnIoPlatform platform;
        private readonly Action<Action> schedule;
        private PawnIoStatus status;

        public PawnIoDependency(bool supported, IPawnIoPlatform platform, Action<Action> schedule = null)
        {
            this.supported = supported;
            this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
            this.schedule = schedule ?? (work => { Task.Run(work); });
            status = new PawnIoStatus { Supported = supported, InstallState = "unknown",
                Message = supported ? "PawnIO sin comprobar." : "PawnIO no disponible para este equipo." };
        }
        public PawnIoStatus GetStatus() { lock (gate) return status.Copy(); }
        public PawnIoStatus Refresh()
        {
            lock (gate)
            {
                if (!supported || status.Busy) return status.Copy();
                try { status = ReadStatus(); }
                catch { status = new PawnIoStatus { Supported = true, InstallState = "error",
                    Message = "No se pudo comprobar PawnIO. Vuelve a consultar antes de instalar.", CapturedAtUtc = DateTime.UtcNow.ToString("O") }; }
                return status.Copy();
            }
        }
        private PawnIoStatus ReadStatus()
        {
            var value = platform.Inspect()?.Copy() ?? throw new InvalidOperationException();
            value.Supported = true;
            value.Busy = false;
            value.CapturedAtUtc = DateTime.UtcNow.ToString("O");
            Version version;
            bool known = Version.TryParse(value.Version, out version);
            value.Version = known ? version.ToString() : null;
            value.Usable = value.Installed && value.Usable && known && version >= PawnIoPackage.MinimumVersion;
            // Preserve an existing compatible/newer dependency, including an
            // installation that needs manual repair. Never downgrade on an error.
            value.CanInstall = !value.Installed || (known && version < PawnIoPackage.MinimumVersion);
            value.InstallState = value.Usable ? "ready" : value.CanInstall ? "missing" : "review";
            value.Message = value.Usable ? "PawnIO " + value.Version + " detectado. La lectura de frecuencias CPU aún no está disponible."
                : value.CanInstall ? "Se necesita PawnIO 2.2 o posterior para la futura lectura de frecuencias CPU. Puedes abrir su instalador oficial."
                : "Hay una instalación de PawnIO que requiere revisión. Se conserva; comprueba el controlador o consulta su sitio oficial.";
            return value;
        }
        public PawnIoStatus BeginInstall()
        {
            lock (gate)
            {
                if (!supported || status.Busy) return status.Copy();
                Refresh(); // Revalidate external installations just before scheduling.
                if (!status.CanInstall) return status.Copy();
                status.Busy = true; status.CanInstall = false; status.InstallState = "downloading";
                status.Message = "Descargando y comprobando el instalador oficial de PawnIO…";
                try { schedule(InstallWorker); }
                catch { FinishFailure("failed", "No se pudo iniciar la preparación del instalador de PawnIO."); }
                return status.Copy();
            }
        }
        private void InstallWorker()
        {
            try
            {
                // A dependency may have been installed while this worker queued.
                lock (gate)
                {
                    var current = ReadStatus();
                    if (!current.CanInstall) { status = current; return; }
                }
                platform.Install(phase => { lock (gate) {
                    status.InstallState = phase == "installer-open" ? phase : "downloading";
                    status.Message = phase == "installer-open" ? "Instalador de PawnIO abierto. Completa o cancela su asistente."
                        : "Descargando y comprobando el instalador oficial de PawnIO…";
                } });
                lock (gate)
                {
                    status = ReadStatus();
                    if (!status.Usable) {
                        status.InstallState = "not-confirmed";
                        status.Message = "El instalador terminó; no se confirmó PawnIO listo. Puedes volver a comprobarlo.";
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { lock (gate) FinishFailure("cancelled", "Se canceló la apertura del instalador de PawnIO."); }
            catch { lock (gate) FinishFailure("failed", "No se pudo descargar, comprobar o abrir el instalador oficial de PawnIO. Vuelve a consultar su estado."); }
        }
        private void FinishFailure(string state, string message)
        {
            // Error does not grant a second installer launch without a fresh probe.
            status.Busy = status.CanInstall = false; status.InstallState = state; status.Message = message;
        }
    }
}
