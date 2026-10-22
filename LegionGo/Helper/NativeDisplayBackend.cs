// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LegionTweaks
{
    internal sealed class NativeDisplayBackend : IDisplayBackend
    {
        internal const uint RequiredFields = 0x00040000 | 0x00080000 | 0x00100000 | 0x00400000;
        internal const uint ChangeFields = RequiredFields | 0x20 | 0x80 | 0x00200000 | 0x20000000;
        internal const uint TestFlag = 2;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Device
        {
            public uint Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Mode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint Bits, Width, Height, Flags, Frequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
            internal DisplayMode PublicMode => new DisplayMode { Width = checked((int)Width), Height = checked((int)Height), Frequency = checked((int)Frequency) };
        }
        public DisplaySnapshot Query()
        {
            var devices = Devices(null).Where(x => (x.Flags & 1) != 0).ToArray();
            var primary = devices.Where(x => (x.Flags & 4) != 0).ToArray();
            if (primary.Length != 1) throw new InvalidOperationException("Windows no identificó una única pantalla principal.");
            Device device = primary[0];
            var monitors = Devices(device.Name, 1).Where(x => (x.Flags & 1) != 0).ToArray();
            Mode current = Read(device.Name, -1);
            var native = Modes(device.Name).Where(x => Usable(x) && x.Orientation == current.Orientation
                && x.Bits == current.Bits && x.Flags == current.Flags).ToArray();
            string restriction = null;
            if (devices.Length != 1 || monitors.Length != 1) restriction = "Esta versión solo cambia una pantalla activa, sin duplicación ni extensión.";
            else if ((device.Flags & (8u | 0x04000000u)) != 0 || GetSystemMetrics(0x1000) != 0 || GetSystemMetrics(0x2001) != 0)
                restriction = "Los cambios de pantalla no están disponibles en una sesión remota o pantalla virtual.";
            else if (string.IsNullOrWhiteSpace(device.Id) || string.IsNullOrWhiteSpace(device.Key) || string.IsNullOrWhiteSpace(monitors[0].Id))
                restriction = "La identidad de la pantalla no se pudo verificar.";
            else if (!Usable(current) || (current.Fields & 0x80) == 0 || current.DriverExtra != 0 || native.Length == 0)
                restriction = "El controlador no informó un modo de escritorio compatible con esta operación.";
            else if (!Interactive()) restriction = "Desbloquea el escritorio de Windows para probar un modo.";
            var publicModes = native.Select(x => x.PublicMode).GroupBy(x => x.ToString()).Select(x => x.First())
                .OrderByDescending(x => x.Width).ThenByDescending(x => x.Height).ThenByDescending(x => x.Frequency).ToArray();
            if (publicModes.Length > 256) restriction = "La lista de modos excede el límite de esta versión; solo se muestra un diagnóstico parcial.";
            var info = new DisplayInfo { DeviceName = device.Name, Adapter = device.Description,
                Monitor = monitors.Length == 1 ? monitors[0].Description : "Monitor sin identificar o duplicado",
                ActiveDisplays = devices.Length, Orientation = checked((int)current.Orientation * 90), Current = current.PublicMode,
                Modes = publicModes.Take(256).ToArray(),
                CanChange = restriction == null, Restriction = restriction, CapturedAtUtc = DateTime.UtcNow.ToString("o") };
            string topology = string.Join("|", devices.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name + ":" + x.Id + ":" + x.Key));
            string identity = topology + "|" + string.Join("|", monitors.Select(x => x.Id + ":" + x.Key))
                + "|" + current.PositionX + ":" + current.PositionY + ":" + current.Bits + ":" + current.Flags + ":" + current.FixedOutput
                + ":" + current.SpecVersion + ":" + current.DriverVersion + ":" + current.DeviceName;
            return new DisplaySnapshot { Info = info, Identity = identity, NativeMode = Bytes(current) };
        }
        private static bool Usable(Mode mode) => (mode.Fields & RequiredFields) == RequiredFields && mode.DriverExtra == 0
            && mode.Bits == 32 && mode.Width >= 320 && mode.Width <= 16384 && mode.Height >= 320 && mode.Height <= 16384
            && mode.Frequency >= 2 && mode.Frequency <= 1000 && mode.Orientation <= 3 && (mode.Flags & 2) == 0;
        public void Test(DisplaySnapshot original, DisplayMode target) => Change(original.Info.DeviceName, Candidate(original, target), TestFlag);
        public void Apply(DisplaySnapshot original, DisplayMode target)
        {
            Mode candidate = Candidate(original, target);
            RequireOriginal(original);
            Change(original.Info.DeviceName, candidate, 0);
        }
        public void Restore(DisplaySnapshot original, DisplayMode expectedTarget)
        {
            if (!Interactive()) throw new InvalidOperationException("El escritorio local no está disponible para recuperar la pantalla.");
            RequireTarget(original, expectedTarget);
            Mode mode = FromBytes(original.NativeMode);
            if (!Usable(mode) || !mode.PublicMode.Same(original.Info.Current) || checked((int)mode.Orientation * 90) != original.Info.Orientation)
                throw new InvalidOperationException("El modo original guardado no es válido.");
            mode.Fields &= ChangeFields;
            Change(original.Info.DeviceName, mode, TestFlag);
            RequireTarget(original, expectedTarget);
            Change(original.Info.DeviceName, mode, 0);
        }
        private void RequireTarget(DisplaySnapshot original, DisplayMode target)
        {
            var current = Query();
            if (!original.SameDevice(current) || !target.Same(current.Info.Current))
                throw new InvalidOperationException("Cambió la pantalla o su modo; se conserva el respaldo sin sobrescribir el estado actual.");
        }
        private void RequireOriginal(DisplaySnapshot original)
        {
            var fresh = Query();
            if (!fresh.Info.CanChange || !original.SameOriginal(fresh)) throw new InvalidOperationException("Cambió la pantalla antes de aplicar. Consulta los modos de nuevo.");
        }
        private Mode Candidate(DisplaySnapshot original, DisplayMode target)
        {
            RequireOriginal(original);
            Mode saved = FromBytes(original.NativeMode);
            Mode[] candidates = Modes(original.Info.DeviceName).Where(x => Usable(x) && x.Orientation == saved.Orientation
                && x.Bits == saved.Bits && x.Flags == saved.Flags && x.PublicMode.Same(target)).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("El modo ya no está enumerado por Windows.");
            Mode mode = candidates[0];
            mode.PositionX = saved.PositionX; mode.PositionY = saved.PositionY; mode.Orientation = saved.Orientation;
            mode.FixedOutput = saved.FixedOutput;
            mode.Fields = (mode.Fields & ChangeFields) | (saved.Fields & (0x20u | 0x80u | 0x20000000u));
            return mode;
        }
        private static IEnumerable<Device> Devices(string parent, uint flags = 0)
        {
            for (uint i = 0; i < 128; i++)
            {
                var device = new Device { Size = (uint)Marshal.SizeOf(typeof(Device)) };
                if (!EnumDisplayDevicesW(parent, i, ref device, flags)) yield break;
                yield return device;
            }
            throw new InvalidOperationException("La lista de pantallas excede el límite de consulta.");
        }
        private static Mode Read(string device, int index)
        {
            var mode = new Mode { Size = (ushort)Marshal.SizeOf(typeof(Mode)) };
            if (!EnumDisplaySettingsExW(device, index, ref mode, 0)) throw new InvalidOperationException("Windows no pudo leer el modo actual de la pantalla.");
            return mode;
        }
        private static IEnumerable<Mode> Modes(string device)
        {
            // Flags 0 exclude raw/unsafe and other-orientation modes. Index 0 refreshes Windows' cache.
            for (int i = 0; i < 4096; i++)
            {
                var mode = new Mode { Size = (ushort)Marshal.SizeOf(typeof(Mode)) };
                if (!EnumDisplaySettingsExW(device, i, ref mode, 0)) yield break;
                yield return mode;
            }
            throw new InvalidOperationException("La lista de modos excede el límite de consulta.");
        }
        private static void Change(string device, Mode mode, uint flags)
        {
            if (!Interactive()) throw new InvalidOperationException("El escritorio local no está disponible para cambiar la pantalla.");
            int code = ChangeDisplaySettingsExW(device, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
            if (code != 0) throw new InvalidOperationException("Windows rechazó el modo de pantalla (código " + code + "). No se solicitó un reinicio.");
        }
        internal static byte[] Bytes(Mode mode)
        {
            IntPtr memory = Marshal.AllocHGlobal(220);
            try { Marshal.StructureToPtr(mode, memory, false); var bytes = new byte[220]; Marshal.Copy(memory, bytes, 0, bytes.Length); return bytes; }
            finally { Marshal.FreeHGlobal(memory); }
        }
        internal static Mode FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 220) throw new InvalidOperationException("Respaldo DEVMODE inválido.");
            IntPtr memory = Marshal.AllocHGlobal(220);
            try
            {
                Marshal.Copy(bytes, 0, memory, 220); var mode = Marshal.PtrToStructure<Mode>(memory);
                if (mode.Size != 220 || mode.DriverExtra != 0) throw new InvalidOperationException("Tamaño DEVMODE incompatible.");
                return mode;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        internal static bool Interactive()
        {
            if (GetSystemMetrics(0x1000) != 0 || GetSystemMetrics(0x2001) != 0) return false;
            IntPtr desktop = OpenInputDesktop(0, false, 1);
            if (desktop == IntPtr.Zero) return false;
            try
            {
                uint needed; var name = new StringBuilder(256);
                return GetUserObjectInformationW(desktop, 2, name, 512, out needed) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseDesktop(desktop); }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool EnumDisplayDevicesW(string device, uint index, ref Device result, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool EnumDisplaySettingsExW(string device, int index, ref Mode mode, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ChangeDisplaySettingsExW(string device, ref Mode mode, IntPtr window, uint flags, IntPtr parameter);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder name, uint length, out uint needed);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CloseDesktop(IntPtr desktop);
    }
}
