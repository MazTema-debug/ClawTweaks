// SPDX-License-Identifier: AGPL-3.0-only
// SDL2 public gamecontroller API; Windows SetupAPI identifies the physical XUSB endpoint.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LegionTweaks
{
    internal static class ControllerDevices
    {
        internal static bool IsLegion(ushort vendor, ushort product) => vendor == 0x17ef && (product == 0x6182 || product == 0x61eb);
        internal static bool IsLegionInstance(string id) => id != null &&
            (id.StartsWith(@"USB\VID_17EF&PID_6182", StringComparison.OrdinalIgnoreCase)
             || id.StartsWith(@"USB\VID_17EF&PID_61EB", StringComparison.OrdinalIgnoreCase));
        internal static string FindPhysicalInstance()
        {
            var guid = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB"); // GUID_DEVINTERFACE_XUSB_DEVICE
            var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw new Win32Exception();
            var matches = new List<string>();
            try
            {
                for (uint index = 0; ; index++)
                {
                    var face = new DeviceInterface { Size = Marshal.SizeOf(typeof(DeviceInterface)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref face))
                    { if (Marshal.GetLastWin32Error() == 259) break; throw new Win32Exception(); }
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(set, ref face, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required < 8 || required > 65536) throw new InvalidOperationException("Identidad de mando inválida.");
                    var buffer = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                        var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                        if (!SetupDiGetDeviceInterfaceDetailInfo(set, ref face, buffer, required, out required, ref info)) throw new Win32Exception();
                        var id = new StringBuilder(1024);
                        if (!SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out required)) throw new Win32Exception();
                        if (IsLegionInstance(id.ToString())) matches.Add(id.ToString());
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            matches = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Se requiere un único mando XInput físico de la Legion Go. Conecta los mandos y desactiva el modo FPS.");
            return matches[0];
        }
        [StructLayout(LayoutKind.Sequential)] private struct DeviceInterface { internal int Size; internal Guid Guid; internal uint Flags; internal IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { internal int Size; internal Guid ClassGuid; internal uint DevInst; internal IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref DeviceInterface face);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterface face, IntPtr detail, uint size, out uint required, IntPtr info);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetailInfo(IntPtr set, ref DeviceInterface face, IntPtr detail, uint size, out uint required, ref DeviceInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out uint required);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
    internal sealed class SdlController : IDisposable
    {
        private IntPtr pad;
        private bool initialized;
        internal void Open()
        {
            Native.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            Native.SDL_SetHint("SDL_JOYSTICK_HIDAPI", "0");
            Native.SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
            Native.SDL_SetHint("SDL_JOYSTICK_WGI", "0");
            if (Native.SDL_InitSubSystem(0x2000) != 0) throw new InvalidOperationException("No se pudo iniciar la lectura SDL del mando.");
            initialized = true;
            // XInput is used by SDL here. Vendor/product gating excludes our Xbox virtual target.
            var candidates = new List<int>();
            Native.SDL_PumpEvents();
            for (int i = 0; i < Native.SDL_NumJoysticks(); i++)
                if (Native.SDL_IsGameController(i) != 0 && ControllerDevices.IsLegion(Native.SDL_JoystickGetDeviceVendor(i), Native.SDL_JoystickGetDeviceProduct(i))) candidates.Add(i);
            if (candidates.Count != 1) throw new InvalidOperationException("SDL no identificó un único mando Lenovo. Cierra otros remapeadores y conecta los mandos en modo XInput.");
            pad = Native.SDL_GameControllerOpen(candidates[0]);
            if (pad == IntPtr.Zero) throw new InvalidOperationException("No se pudo abrir el mando físico.");
        }
        internal bool Read(out GamepadFrame frame)
        {
            frame = new GamepadFrame(); Native.SDL_PumpEvents(); Native.SDL_GameControllerUpdate();
            if (pad == IntPtr.Zero || Native.SDL_GameControllerGetAttached(pad) == 0) return false;
            // SDL button order differs from XInput. Values below are the public SDL2 enum.
            int[] sdlButtons = { 0, 1, 2, 3, 9, 10, 4, 6, 7, 8, 11, 12, 13, 14, 5 };
            for (int i = 0; i < sdlButtons.Length; i++) if (Native.SDL_GameControllerGetButton(pad, sdlButtons[i]) != 0) frame.Buttons |= (ushort)ControllerLayout.Buttons[i];
            frame.LX = Native.SDL_GameControllerGetAxis(pad, 0); frame.LY = Invert(Native.SDL_GameControllerGetAxis(pad, 1));
            frame.RX = Native.SDL_GameControllerGetAxis(pad, 2); frame.RY = Invert(Native.SDL_GameControllerGetAxis(pad, 3));
            frame.LT = (byte)(Math.Max(0, (int)Native.SDL_GameControllerGetAxis(pad, 4)) * 255 / 32767);
            frame.RT = (byte)(Math.Max(0, (int)Native.SDL_GameControllerGetAxis(pad, 5)) * 255 / 32767);
            return true;
        }
        private static short Invert(short value) => value == short.MinValue ? short.MaxValue : value == short.MaxValue ? short.MinValue : (short)-value;
        internal void Rumble(byte low, byte high, int percent) { if (pad != IntPtr.Zero) Native.SDL_GameControllerRumble(pad, (ushort)(low * 257 * percent / 100), (ushort)(high * 257 * percent / 100), 200); }
        public void Dispose()
        {
            if (pad != IntPtr.Zero) { Rumble(0, 0, 0); Native.SDL_GameControllerClose(pad); pad = IntPtr.Zero; }
            if (initialized) { Native.SDL_QuitSubSystem(0x2000); initialized = false; }
        }
        private static class Native
        {
            private const string Dll = "SDL2.dll";
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_SetHint(string name, string value);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_InitSubSystem(uint flags);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_QuitSubSystem(uint flags);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_PumpEvents();
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_NumJoysticks();
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_IsGameController(int index);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ushort SDL_JoystickGetDeviceVendor(int index);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ushort SDL_JoystickGetDeviceProduct(int index);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_GameControllerOpen(int index);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_GameControllerClose(IntPtr pad);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_GameControllerGetAttached(IntPtr pad);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_GameControllerUpdate();
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern byte SDL_GameControllerGetButton(IntPtr pad, int button);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern short SDL_GameControllerGetAxis(IntPtr pad, int axis);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_GameControllerRumble(IntPtr pad, ushort low, ushort high, uint ms);
        }
    }
}
