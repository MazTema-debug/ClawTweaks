// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace LegionTweaks
{
    public enum RtssLevel { Minimal, Basic, Full, Custom }
    public enum RtssOrientation { Vertical, Horizontal }
    public enum RtssMetric
    {
        FrameRate, FrameTime, CpuTemperature, GpuTemperature, CpuPower, GfxPower,
        AsicPower, GpuLoad, GpuClock, GpuVoltage, SocVoltage, Fan, Battery, CustomLimits
    }

    // Only enumerated presentation choices cross IPC. No user text, RTSS code,
    // paths, activation preference or sensor polling interval is accepted.
    [DataContract]
    public sealed class RtssAppearance
    {
        public const int MaximumBytes = 2048;
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public RtssLevel Level { get; set; } = RtssLevel.Full;
        [DataMember(IsRequired = true)] public RtssOrientation Orientation { get; set; }
        [DataMember(IsRequired = true)] public int Scale { get; set; } = 100;
        [DataMember(IsRequired = true)] public RtssMetric[] Metrics { get; set; } = new[]
            { RtssMetric.FrameRate, RtssMetric.FrameTime, RtssMetric.CpuTemperature, RtssMetric.GpuTemperature, RtssMetric.Battery };

        public void Validate()
        {
            if (Version != 1 || !Enum.IsDefined(typeof(RtssLevel), Level)
                || !Enum.IsDefined(typeof(RtssOrientation), Orientation))
                throw new ArgumentException("Formato de apariencia RTSS incompatible.");
            if (Scale != 75 && Scale != 100 && Scale != 125 && Scale != 150)
                throw new ArgumentException("Selecciona un tamaño de 75, 100, 125 o 150 %.");
            if (Metrics == null || Metrics.Length == 0 || Metrics.Length > 14
                || Metrics.Any(x => !Enum.IsDefined(typeof(RtssMetric), x)) || Metrics.Distinct().Count() != Metrics.Length)
                throw new ArgumentException("Selecciona entre 1 y 14 métricas distintas para el nivel personalizado.");
        }
        public RtssAppearance Copy()
        {
            Validate();
            return new RtssAppearance { Version = Version, Level = Level, Orientation = Orientation,
                Scale = Scale, Metrics = (RtssMetric[])Metrics.Clone() };
        }
        public RtssMetric[] EffectiveMetrics()
        {
            Validate();
            if (Level == RtssLevel.Minimal) return new[] { RtssMetric.FrameRate, RtssMetric.FrameTime };
            if (Level == RtssLevel.Basic) return new[] { RtssMetric.FrameRate, RtssMetric.FrameTime,
                RtssMetric.CpuTemperature, RtssMetric.GpuTemperature, RtssMetric.Battery };
            return Level == RtssLevel.Custom ? (RtssMetric[])Metrics.Clone() : (RtssMetric[])Enum.GetValues(typeof(RtssMetric));
        }
        public static string Label(RtssMetric metric)
        {
            switch (metric)
            {
                case RtssMetric.FrameRate: return "FPS (RTSS)";
                case RtssMetric.FrameTime: return "Tiempo por fotograma (RTSS)";
                case RtssMetric.CpuTemperature: return "Temperatura CPU";
                case RtssMetric.GpuTemperature: return "Temperatura GPU";
                case RtssMetric.CpuPower: return "Potencia CPU";
                case RtssMetric.GfxPower: return "Potencia GFX";
                case RtssMetric.AsicPower: return "Potencia ASIC";
                case RtssMetric.GpuLoad: return "Carga GPU";
                case RtssMetric.GpuClock: return "Frecuencia GPU";
                case RtssMetric.GpuVoltage: return "Voltaje GPU";
                case RtssMetric.SocVoltage: return "Voltaje SoC";
                case RtssMetric.Fan: return "Ventilador";
                case RtssMetric.Battery: return "Batería y cargador";
                case RtssMetric.CustomLimits: return "Límites SPL / SPPT / FPPT";
                default: throw new ArgumentException("Métrica desconocida.");
            }
        }
        public string Encode()
        {
            Validate(); string json = Protocol.Encode(this);
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Apariencia RTSS demasiado grande.");
            return json;
        }
        public static RtssAppearance Decode(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidDataException("Apariencia RTSS vacía o demasiado grande.");
            try
            {
                var value = Protocol.Decode<RtssAppearance>(json);
                if (value == null) throw new ArgumentException("Apariencia vacía.");
                value.Validate(); return value;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is SerializationException)
            { throw new InvalidDataException("No se pudo leer la apariencia RTSS guardada.", ex); }
        }
    }

    public interface IRtssAppearanceControl
    {
        void Configure(RtssAppearance appearance);
    }
}
