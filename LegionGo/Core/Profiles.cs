// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class ProfileDefinition
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public int? Mode { get; set; }
        [DataMember] public int[] Limits { get; set; }
        [DataMember] public int? BoostAC { get; set; }
        [DataMember] public int? BoostDC { get; set; }
        [DataMember] public int? EppAC { get; set; }
        [DataMember] public int? EppDC { get; set; }
        [DataMember] public int? WindowsMode { get; set; }
        // Null means leave the running controller alone; Enabled=false explicitly stops it.
        [DataMember] public ControllerLayout Controller { get; set; }
        public ProfileDefinition Copy() => Protocol.Decode<ProfileDefinition>(Protocol.Encode(this));
        public void Validate()
        {
            Guid id;
            if (!Guid.TryParse(Id, out id) || string.IsNullOrWhiteSpace(Name) || Name.Length > 40 || Name.Any(char.IsControl))
                throw new ArgumentException("El perfil requiere un identificador válido y un nombre de 1–40 caracteres.");
            if (Mode.HasValue && Mode != 1 && Mode != 2 && Mode != 3 && Mode != 255) throw new ArgumentException("Modo Lenovo inválido.");
            if (Mode == 255) ValidateLimits(Limits);
            else if (Limits != null) throw new ArgumentException("Los límites requieren modo Personalizado.");
            foreach (var boost in new[] { BoostAC, BoostDC })
                if (boost.HasValue && boost != 0 && boost != 2) throw new ArgumentException("Boost debe ser 0 o 2.");
            foreach (var epp in new[] { EppAC, EppDC })
                if (epp.HasValue && (epp < 0 || epp > 100)) throw new ArgumentException("EPP debe estar entre 0 y 100.");
            if (WindowsMode.HasValue && (WindowsMode < 0 || WindowsMode > 2)) throw new ArgumentException("Modo Windows inválido.");
            Controller?.Validate();
        }
        public static void ValidateLimits(int[] values)
        {
            if (values == null || values.Length != 3 || values.Any(x => x < 5 || x > 30) || values[0] > values[1] || values[1] > values[2])
                throw new ArgumentException("Se requiere 5 ≤ SPL ≤ SPPT ≤ FPPT ≤ 30 W.");
        }
    }
    [DataContract]
    public sealed class LegacyPowerProfile
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public int[] Limits { get; set; }
    }
    [DataContract]
    public sealed class ProfileCatalog
    {
        [DataMember] public int Version { get; set; } = 2;
        [DataMember] public List<ProfileDefinition> Profiles { get; set; } = new List<ProfileDefinition>();
        public void Validate()
        {
            if (Version != 2 || Profiles == null || Profiles.Count > 20) throw new ArgumentException("Archivo de perfiles incompatible o demasiado grande.");
            foreach (var p in Profiles) { if (p == null) throw new ArgumentException("Perfil vacío."); p.Validate(); }
            if (Profiles.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Count
                || Profiles.Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Count)
                throw new ArgumentException("Hay perfiles con identificadores o nombres repetidos.");
        }
        public ProfileCatalog Upsert(ProfileDefinition profile)
        {
            profile.Validate(); Validate();
            var next = Protocol.Decode<ProfileCatalog>(Protocol.Encode(this));
            next.Profiles.RemoveAll(x => x.Id == profile.Id);
            next.Profiles.Add(profile.Copy()); next.Validate(); return next;
        }
        public static ProfileCatalog Migrate(string json)
        {
            var legacy = Protocol.Decode<List<LegacyPowerProfile>>(json);
            if (legacy == null || legacy.Count > 20) throw new ArgumentException("Perfiles antiguos inválidos.");
            var result = new ProfileCatalog();
            foreach (var p in legacy)
            {
                if (p == null) throw new ArgumentException("Perfil antiguo vacío.");
                result.Profiles.Add(new ProfileDefinition { Id = Guid.NewGuid().ToString("D"), Name = p.Name, Mode = 255, Limits = p.Limits });
            }
            result.Validate(); return result;
        }
    }
}
