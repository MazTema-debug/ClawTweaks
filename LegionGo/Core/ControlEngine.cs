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
        private readonly List<OriginalValue> originals;
        private readonly object gate = new object();

        public ControlEngine(IHardware hardware, IJournal journal)
        {
            this.hardware = hardware;
            this.journal = journal;
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
                        case "restore": Restore(); break;
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
            if (values == null || values.Length != 3 || values.Any(x => x < MinWatts || x > MaxWatts)
                || values[0] > values[1] || values[1] > values[2])
                throw new ArgumentException("Se requiere 5 ≤ SPL ≤ SPPT ≤ FPPT ≤ 30 W.");
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
