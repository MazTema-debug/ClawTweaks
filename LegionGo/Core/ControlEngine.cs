// SPDX-License-Identifier: AGPL-3.0-only
// Lenovo IDs and the Custom-mode transition are derived from GoTweaks; see SOURCES.md.
using System;
using System.Collections.Generic;
using System.Linq;

namespace LegionTweaks
{
    public interface IHardware
    {
        DeviceIdentity Identity { get; }
        bool OnAc { get; }
        string ActiveScheme { get; }
        int Read(Address address);
        void Write(Address address, int value);
        Reading[] ReadTelemetry();
        void Wait(int milliseconds);
    }
    public interface IJournal
    {
        IList<OriginalValue> Load();
        void Save(IList<OriginalValue> originals);
    }

    public sealed class ControlEngine
    {
        public const int MinWatts = 5;
        public const int MaxWatts = 30; // Deliberately conservative, including SPPT/FPPT.
        private readonly IHardware hardware;
        private readonly IJournal journal;
        private readonly IControllerService controller;
        private readonly List<OriginalValue> originals;
        private readonly object gate = new object();

        public ControlEngine(IHardware hardware, IJournal journal, IControllerService controller = null)
        {
            this.hardware = hardware;
            this.journal = journal;
            this.controller = controller;
            originals = new List<OriginalValue>(journal.Load());
        }
        private Address GetAddress(Setting setting, bool ac = false)
        {
            bool cpu = setting == Setting.CpuBoost || setting == Setting.Epp;
            return new Address { Setting = setting, Ac = cpu && ac, Scheme = cpu ? hardware.ActiveScheme : null };
        }
        public Response Execute(Request request)
        {
            lock (gate)
            {
                try
                {
                    if (request == null || request.Version != Protocol.Version)
                        throw new InvalidOperationException("La versión del widget y del servicio no coincide.");
                    if (request.Operation == "status")
                        return Result(true, "Estado leído del equipo.");
                    if (request.Operation == "telemetry")
                        return new Response { Success = true, Message = "Telemetría leída del equipo.", State = ReadSnapshot(true) };
                    if (!hardware.Identity.IsSupported)
                        throw new InvalidOperationException("Solo se admite la Legion Go 8APU1 con Ryzen Z1 Extreme.");
                    switch (request.Operation)
                    {
                        case "tdp": ApplyTdp(request.Values); break;
                        case "set": ApplySetting(request); break;
                        case "profile_apply": ApplyProfile(request.Profile); break;
                        case "mouse_set":
                            if (request.Values == null || request.Values.Length != 3 || request.Values[0] < 0 || request.Values[0] > 1
                                || request.Values[1] < 1 || request.Values[1] > 20 || request.Values[2] < 5 || request.Values[2] > 30)
                                throw new ArgumentException("Selecciona estado, sensibilidad y zona muerta válidos para el mouse.");
                            var mouse = controller as IStickMouseControllerService;
                            if (mouse == null) throw new InvalidOperationException("Actualiza el helper para usar el mouse con stick derecho.");
                            mouse.SetMouse(request.Values[0] == 1, request.Values[1], request.Values[2]);
                            return Result(true, "Ajustes del mouse actualizados; no se aplicaron ajustes de energía.");
                        case "controller_split":
                        case "controller_split_general":
                            if (request.Profile != null || request.Values != null || request.Key != null || request.Ac
                                || request.RtssAppearance != null || request.Display != null)
                                throw new ArgumentException("Dos jugadores usa una distribución integrada sin ajustes editables.");
                            if (request.Operation == "controller_split_general")
                            {
                                var general = controller as IGeneralSplitControllerService;
                                if (general == null) throw new InvalidOperationException("Actualiza el helper a 0.1.34 para usar dos jugadores con botones A/B/X/Y.");
                                general.PrepareGeneralSplitControllers();
                            }
                            else
                            {
                                var split = controller as ISplitControllerService;
                                if (split == null) throw new InvalidOperationException("Actualiza el helper para usar dos jugadores.");
                                split.PrepareSplitControllers();
                            }
                            return Result(true, "Preparando dos jugadores; no se aplicaron ajustes de energía.");
                        case "controller_desktop":
                            if (request.Profile != null || request.Values != null)
                                throw new ArgumentException("Desktop es un perfil integrado y no admite ajustes editables.");
                            var desktop = controller as IModePlayerOneControllerService;
                            if (desktop == null) throw new InvalidOperationException("Actualiza el helper para usar Desktop.");
                            desktop.PreparePlayerOneMode(DesktopControllerProfile.CreateLayout());
                            return Result(true, "Desktop iniciado como mando; no se aplicaron ajustes de energía.");
                        case "controller_player_one":
                        case "controller_player_one_auto":
                        case "controller_player_one_busy":
                        case "controller_player_one_mode":
                            if (request.Profile == null) throw new ArgumentException("Selecciona un perfil guardado con mando virtual.");
                            request.Profile.Validate();
                            if (request.Profile.Controller?.Enabled != true) throw new ArgumentException("El perfil seleccionado no activa el mando virtual.");
                            if (request.Operation == "controller_player_one_mode")
                            {
                                var mode = controller as IModePlayerOneControllerService;
                                if (mode == null) throw new InvalidOperationException("Actualiza el helper a 0.1.22 para preparar el jugador 1 mediante cambio de modo.");
                                mode.PreparePlayerOneMode(request.Profile.Controller.Copy());
                            }
                            else if (request.Operation == "controller_player_one_busy")
                            {
                                var busy = controller as IBusyPlayerOneControllerService;
                                if (busy == null) throw new InvalidOperationException("Actualiza el helper para reconectar con lectores abiertos.");
                                busy.PreparePlayerOneWithBusyReaders(request.Profile.Controller.Copy());
                            }
                            else if (request.Operation == "controller_player_one_auto")
                            {
                                var automatic = controller as IAutomaticPlayerOneControllerService;
                                if (automatic == null) throw new InvalidOperationException("Actualiza el helper a 0.1.11 para preparar automáticamente el jugador 1.");
                                automatic.PreparePlayerOneAutomatic(request.Profile.Controller.Copy());
                            }
                            else
                            {
                                var preparation = controller as IPlayerOneControllerService;
                                if (preparation == null) throw new InvalidOperationException("Actualiza el helper para preparar el jugador 1.");
                                preparation.PreparePlayerOne(request.Profile.Controller.Copy());
                            }
                            return Result(true, "Preparación iniciada. Sigue las instrucciones de la pestaña Mando; no se aplicaron ajustes de energía.");
                        case "controller_stop": controller?.Apply(new ControllerLayout()); break;
                        case "restore": controller?.Apply(new ControllerLayout()); Restore(); break;
                        default: throw new ArgumentException("Operación desconocida.");
                    }
                    return Result(true, request.Operation == "restore" ? "Valores originales restaurados." : "Cambio aplicado y verificado.");
                }
                catch (Exception ex) { return Result(false, ex.Message); }
            }
        }
        private Response Result(bool success, string message)
        {
            return new Response { Success = success, Message = message, State = ReadSnapshot() };
        }
        private Snapshot ReadSnapshot(bool telemetryOnly = false)
        {
            var readings = new List<Reading>();
            if (hardware.Identity.IsSupported)
            {
                var settings = telemetryOnly ? new[] { Setting.Mode, Setting.Spl, Setting.Sppt, Setting.Fppt }
                    : (Setting[])Enum.GetValues(typeof(Setting));
                foreach (Setting setting in settings)
                {
                    bool cpu = setting == Setting.CpuBoost || setting == Setting.Epp;
                    foreach (bool ac in cpu ? new[] { false, true } : new[] { false })
                    {
                        string key = setting + (cpu ? (ac ? "AC" : "DC") : "");
                        try { readings.Add(new Reading { Key = key, Value = hardware.Read(GetAddress(setting, ac)) }); }
                        catch (Exception ex) { readings.Add(new Reading { Key = key, Error = ex.Message }); }
                    }
                }
                try { readings.AddRange(hardware.ReadTelemetry()); }
                catch (Exception ex) { readings.Add(new Reading { Key = "Telemetry", Error = ex.Message }); }
            }
            return new Snapshot { Device = hardware.Identity, Supported = hardware.Identity.IsSupported,
                OnAc = hardware.OnAc, Values = readings.ToArray(), SavedOriginals = originals.Count,
                CapturedAtUtc = DateTime.UtcNow.ToString("O") };
        }
        private void Remember(IEnumerable<OriginalValue> values)
        {
            var updated = new List<OriginalValue>(originals);
            foreach (var value in values)
                if (!updated.Any(x => x.Address.Id == value.Address.Id)) updated.Add(value);
            // Persistence must succeed BEFORE the first hardware write.
            journal.Save(updated);
            originals.Clear();
            originals.AddRange(updated);
        }
        private OriginalValue Capture(Address address) => new OriginalValue { Address = address, Value = hardware.Read(address) };
        private void WriteVerified(Address address, int value)
        {
            // Re-sending Custom mode can reset Lenovo's power registers. Do not undo a
            // just-restored tuple by writing the same mode again at the end of restore.
            if (hardware.Read(address) == value) return;
            hardware.Write(address, value);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                hardware.Wait(100); // Lenovo mode changes can take ~400 ms.
                if (hardware.Read(address) == value) return;
            }
            throw new InvalidOperationException(address.Setting + ": el firmware no confirmó el valor solicitado.");
        }
        private static void ValidateMode(int value)
        {
            if (value != 1 && value != 2 && value != 3 && value != 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Modo Lenovo inválido.");
        }
        public static void ValidateTdp(int[] values)
        {
            ProfileDefinition.ValidateLimits(values);
        }
        private void ApplyProfile(ProfileDefinition profile)
        {
            if (controller?.GetStatus()?.PreparingPlayerOne == true)
                throw new InvalidOperationException("Termina o cancela la preparación del jugador 1 antes de aplicar un perfil.");
            if (profile == null) throw new ArgumentException("Falta el perfil.");
            profile.Validate();
            if (profile.Controller != null)
            {
                if (controller == null && profile.Controller.Enabled) throw new InvalidOperationException("Servicio de mando no disponible.");
                controller?.Preflight(profile.Controller);
            }
            var oldLayout = controller?.Layout?.Copy() ?? new ControllerLayout();
            // Capture the active power plan once. Every CPU write AND rollback uses that plan.
            string scheme = hardware.ActiveScheme;
            var targets = new List<OriginalValue>();
            Action<Setting, int?, bool> add = (setting, value, ac) => {
                if (value.HasValue) targets.Add(new OriginalValue { Address = new Address { Setting = setting, Ac = ac,
                    Scheme = setting == Setting.CpuBoost || setting == Setting.Epp ? scheme : null }, Value = value.Value });
            };
            add(Setting.CpuBoost, profile.BoostAC, true); add(Setting.CpuBoost, profile.BoostDC, false);
            add(Setting.Epp, profile.EppAC, true); add(Setting.Epp, profile.EppDC, false);
            add(Setting.OsPowerMode, profile.WindowsMode, false);
            var before = targets.Select(x => Capture(x.Address)).ToList();
            OriginalValue mode = null;
            OriginalValue[] limits = null;
            if (profile.Mode.HasValue)
            {
                mode = Capture(GetAddress(Setting.Mode));
                // Mode transitions can alter registers even when Custom is not selected.
                limits = new[] { Capture(GetAddress(Setting.Spl)), Capture(GetAddress(Setting.Sppt)), Capture(GetAddress(Setting.Fppt)) };
            }
            var backup = before.Concat(limits ?? new OriginalValue[0]).ToList();
            if (mode != null) backup.Add(mode);
            if (backup.Count > 0) Remember(backup);
            bool controllerAttempted = false;
            try
            {
                if (mode != null) WriteVerified(mode.Address, profile.Mode.Value);
                if (profile.Mode == 255) WriteLimits(profile.Limits);
                foreach (var target in targets) WriteVerified(target.Address, target.Value);
                if (targets.Any(x => x.Address.Scheme != null) && hardware.ActiveScheme != scheme)
                    throw new InvalidOperationException("Windows cambió el plan de energía mientras se aplicaba el perfil.");
                if (mode != null && hardware.Read(mode.Address) != profile.Mode)
                    throw new InvalidOperationException("El modo Lenovo cambió durante la aplicación.");
                if (profile.Mode == 255 && !new[] { Setting.Spl, Setting.Sppt, Setting.Fppt }.Select(x => hardware.Read(GetAddress(x))).SequenceEqual(profile.Limits))
                    throw new InvalidOperationException("Los límites cambiaron durante la aplicación.");
                foreach (var target in targets)
                    if (hardware.Read(target.Address) != target.Value) throw new InvalidOperationException("Un ajuste del perfil cambió durante la aplicación.");
                if (profile.Controller != null && controller != null) { controllerAttempted = true; controller.Apply(profile.Controller); }
            }
            catch (Exception ex)
            {
                var errors = new List<string>();
                if (controllerAttempted)
                    try { controller.Apply(oldLayout); } catch (Exception e) { errors.Add("Mando: " + e.Message); }
                foreach (var value in before.AsEnumerable().Reverse())
                    try { WriteVerified(value.Address, value.Value); } catch (Exception e) { errors.Add(e.Message); }
                if (mode != null)
                {
                    // Restore the tuple in Custom first; restore the former mode last.
                    try { WriteVerified(mode.Address, 255); WriteLimits(limits.Select(x => x.Value).ToArray()); }
                    catch (Exception e) { errors.Add(e.Message); }
                    try { WriteVerified(mode.Address, mode.Value); } catch (Exception e) { errors.Add(e.Message); }
                }
                throw new InvalidOperationException(ex.Message + (errors.Count == 0 ? " Se restauró la configuración anterior."
                    : " Restauración incompleta: " + string.Join("; ", errors)));
            }
        }
        private void ApplySetting(Request request)
        {
            if (request.Values == null || request.Values.Length != 1)
                throw new ArgumentException("Se requiere un valor.");
            Setting setting;
            if (!Enum.TryParse(request.Key, false, out setting) || !Enum.IsDefined(typeof(Setting), setting)
                || request.Key != setting.ToString()) throw new ArgumentException("Control desconocido.");
            int value = request.Values[0];
            switch (setting)
            {
                case Setting.Mode: ValidateMode(value); break;
                case Setting.FanFullSpeed:
                case Setting.ChargeLimit:
                case Setting.PowerLight:
                    if (value != 0 && value != 1) throw new ArgumentOutOfRangeException(nameof(value));
                    break;
                case Setting.CpuBoost:
                    if (value != 0 && value != 2) throw new ArgumentOutOfRangeException(nameof(value));
                    break;
                case Setting.Epp:
                    if (value < 0 || value > 100) throw new ArgumentOutOfRangeException(nameof(value));
                    break;
                case Setting.OsPowerMode:
                    if (value < 0 || value > 2) throw new ArgumentOutOfRangeException(nameof(value));
                    break;
                default: throw new ArgumentException("Usa la operación TDP para los tres límites juntos.");
            }
            var before = Capture(GetAddress(setting, request.Ac));
            if (before.Value == value) return;
            var captured = new List<OriginalValue> { before };
            if (setting == Setting.Mode && before.Value == 255)
                captured.AddRange(new[] { Capture(GetAddress(Setting.Spl)), Capture(GetAddress(Setting.Sppt)), Capture(GetAddress(Setting.Fppt)) });
            Remember(captured);
            try { WriteVerified(before.Address, value); }
            catch (Exception ex)
            {
                try
                {
                    WriteVerified(before.Address, before.Value);
                    if (captured.Count == 4) WriteLimits(captured.Skip(1).Select(x => x.Value).ToArray());
                }
                catch (Exception rollback) { throw new InvalidOperationException(ex.Message + " Restauración falló: " + rollback.Message); }
                throw new InvalidOperationException(ex.Message + " Se restauró el valor anterior.");
            }
        }
        private void ApplyTdp(int[] values)
        {
            ValidateTdp(values);
            var mode = Capture(GetAddress(Setting.Mode));
            var before = new[] { Capture(GetAddress(Setting.Spl)), Capture(GetAddress(Setting.Sppt)), Capture(GetAddress(Setting.Fppt)) };
            Remember(before.Concat(new[] { mode }));
            bool limitsStarted = false;
            try
            {
                if (mode.Value != 255) WriteVerified(mode.Address, 255);
                limitsStarted = true;
                WriteLimits(values);
            }
            catch (Exception ex)
            {
                var errors = RollbackLimits(limitsStarted ? before : new OriginalValue[0], mode);
                throw new InvalidOperationException(ex.Message + (errors.Count == 0
                    ? " Se restauraron los valores anteriores."
                    : " Restauración incompleta: " + string.Join("; ", errors)));
            }
        }
        private void WriteLimits(int[] values)
        {
            var settings = new[] { Setting.Spl, Setting.Sppt, Setting.Fppt };
            // Lower sustained first; raise peak first. Never intentionally cross the limits.
            var current = settings.Select(x => hardware.Read(GetAddress(x))).ToArray();
            for (int i = 0; i < 3; i++)
                if (values[i] < current[i]) WriteVerified(GetAddress(settings[i]), values[i]);
            for (int i = 2; i >= 0; i--)
                if (values[i] >= current[i]) WriteVerified(GetAddress(settings[i]), values[i]);
            // Check the complete tuple, because firmware may couple the registers.
            for (int i = 0; i < 3; i++)
                if (hardware.Read(GetAddress(settings[i])) != values[i])
                    throw new InvalidOperationException("El firmware modificó uno de los límites TDP.");
        }
        private List<string> RollbackLimits(IEnumerable<OriginalValue> values, OriginalValue mode)
        {
            var errors = new List<string>();
            var limits = values.ToArray();
            if (limits.Length == 3)
                try { WriteLimits(limits.Select(x => x.Value).ToArray()); } catch (Exception ex) { errors.Add(ex.Message); }
            try { WriteVerified(mode.Address, mode.Value); } catch (Exception ex) { errors.Add(ex.Message); }
            return errors;
        }
        private void Restore()
        {
            var errors = new List<string>();
            var tdp = originals.Where(x => x.Address.Setting == Setting.Spl || x.Address.Setting == Setting.Sppt || x.Address.Setting == Setting.Fppt).ToList();
            if (tdp.Count > 0)
            {
                try
                {
                    WriteVerified(GetAddress(Setting.Mode), 255);
                    WriteLimits(new[] { Setting.Spl, Setting.Sppt, Setting.Fppt }
                        .Select(setting => tdp.Single(x => x.Address.Setting == setting).Value).ToArray());
                }
                catch (Exception ex) { errors.Add(ex.Message); }
            }
            foreach (var original in originals.OrderBy(x => x.Address.Setting == Setting.Mode ? 1 : 0))
            {
                if (tdp.Contains(original)) continue;
                try { WriteVerified(original.Address, original.Value); }
                catch (Exception ex) { errors.Add(ex.Message); }
            }
            if (errors.Count > 0) throw new InvalidOperationException("Restauración incompleta; se conserva el respaldo: " + string.Join("; ", errors));
            journal.Save(new List<OriginalValue>());
            originals.Clear();
        }
    }
}
