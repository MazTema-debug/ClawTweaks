// SPDX-License-Identifier: AGPL-3.0-only
// Protocol facts: HHD hwinfo and Lenovo WMI driver discussion; see SOURCES.md.
using System;
using System.Collections.Generic;

namespace LegionTweaks
{
    public interface IActivePowerHardware
    {
        Reading[] ReadActivePowerLimits();
    }

    public static class PowerLimits
    {
        public const int MappingVersion = 1;
        // Disk labels retain the legacy register binding, so .47 can still restore them.
        public const int JournalPhysicalVersion = 2;
        public static bool IsLimit(Setting setting) => setting == Setting.Spl || setting == Setting.Sppt || setting == Setting.Fppt;
        public static uint FeatureId(Setting setting, int mode)
        {
            if (!IsLimit(setting) || (mode != 1 && mode != 2 && mode != 3 && mode != 255))
                throw new ArgumentOutOfRangeException();
            uint kind = setting == Setting.Spl ? 2u : setting == Setting.Sppt ? 1u : 3u;
            return 0x01000000u | (kind << 16) | ((uint)mode << 8);
        }
        // Old journal labels denoted physical registers: old Spl was kind 1, old Sppt kind 2.
        // Copy instead of mutating the caller's list; reads never rewrite the journal.
        public static OriginalValue NormalizeOriginal(OriginalValue value)
        {
            var a = value.Address;
            var setting = a.Setting;
            if (IsLimit(setting) && (value.PowerMappingVersion == 0 || value.PowerMappingVersion == JournalPhysicalVersion))
                setting = setting == Setting.Spl ? Setting.Sppt : setting == Setting.Sppt ? Setting.Spl : setting;
            if (IsLimit(setting) && value.PowerMappingVersion != 0 && value.PowerMappingVersion != MappingVersion
                && value.PowerMappingVersion != JournalPhysicalVersion)
                throw new InvalidOperationException("Respaldo de potencia de una versión no compatible.");
            return new OriginalValue { Address = new Address { Setting = setting, Ac = a.Ac, Scheme = a.Scheme },
                Value = value.Value, PowerMappingVersion = IsLimit(setting) ? MappingVersion : value.PowerMappingVersion };
        }
        public static OriginalValue ForStorage(OriginalValue value)
        {
            var stored = NormalizeOriginal(value);
            if (IsLimit(stored.Address.Setting))
            {
                var setting = stored.Address.Setting;
                stored.Address.Setting = setting == Setting.Spl ? Setting.Sppt : setting == Setting.Sppt ? Setting.Spl : setting;
                stored.PowerMappingVersion = JournalPhysicalVersion;
            }
            return stored;
        }
        public static Reading[] ReadActive(IHardware hardware, Func<Setting, int, int> read)
        {
            var result = new List<Reading>();
            try
            {
                int mode = hardware.Read(new Address { Setting = Setting.Mode });
                bool ac = hardware.OnAc;
                FeatureId(Setting.Spl, mode); // Unknown modes never fall back to FF.
                foreach (var setting in new[] { Setting.Spl, Setting.Sppt, Setting.Fppt })
                {
                    var r = new Reading { Key = "Active" + setting, Unit = "W",
                        Source = "Lenovo GetFeatureValue · modo " + mode + (ac ? " AC" : " DC") };
                    try
                    {
                        r.Value = read(setting, mode);
                        if (r.Value < 1 || r.Value > 100) throw new InvalidOperationException("Límite fuera de rango.");
                    }
                    catch (Exception ex) { r.Value = null; r.Error = ex.Message; }
                    result.Add(r);
                }
                if (mode != hardware.Read(new Address { Setting = Setting.Mode }) || ac != hardware.OnAc)
                    throw new InvalidOperationException("El modo Lenovo o AC/DC cambió durante la lectura; repite la consulta.");
                result.Add(new Reading { Key = "ActiveMode", Value = mode });
                result.Add(new Reading { Key = "ActiveOnAc", Value = ac ? 1 : 0 });
            }
            catch (Exception ex)
            {
                result.Clear();
                foreach (var setting in new[] { Setting.Spl, Setting.Sppt, Setting.Fppt })
                    result.Add(new Reading { Key = "Active" + setting, Error = ex.Message });
            }
            return result.ToArray();
        }
    }
}
