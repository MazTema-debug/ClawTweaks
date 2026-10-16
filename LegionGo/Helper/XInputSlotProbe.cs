// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks
{
    // XInput retains native device access for the process lifetime. A completed,
    // short-lived reader must exit before the parent can request a PNP transition.
    internal static class XInputSlotProbe
    {
        internal static bool[] Parse(string value)
        {
            if (value == null || value.Length != 4)
                throw new InvalidDataException("El lector XInput no devolvió los cuatro puestos.");
            var slots = new bool[4];
            for (int i = 0; i < slots.Length; i++)
            {
                if (value[i] != '0' && value[i] != '1')
                    throw new InvalidDataException("Respuesta inválida del lector XInput.");
                slots[i] = value[i] == '1';
            }
            return slots;
        }

        internal static bool[] Read(WaitHandle canceled, Action checkBudget)
        {
            checkBudget();
            using (var child = Process.Start(new ProcessStartInfo {
                FileName = Process.GetCurrentProcess().MainModule.FileName,
                Arguments = "--controller-slot-probe", UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }))
            {
                var output = Drain(child.StandardOutput, 64);
                var error = Drain(child.StandardError, 1024);
                var clock = Stopwatch.StartNew();
                try
                {
                    while (!child.WaitForExit(25))
                    {
                        checkBudget();
                        if (canceled.WaitOne(0)) throw new OperationCanceledException("Lectura del mando cancelada.");
                        if (clock.ElapsedMilliseconds >= 3000)
                            throw new TimeoutException("El lector XInput no terminó a tiempo; no se reconectó el dispositivo.");
                    }
                    checkBudget();
                    if (!Task.WaitAll(new Task[] { output, error }, 1000))
                        throw new TimeoutException("No se pudo completar la lectura del mando.");
                    if (child.ExitCode != 0)
                        throw new InvalidOperationException("Falló el lector XInput. " + error.Result.Trim());
                    return Parse(output.Result);
                }
                finally
                {
                    // A requested cancellation is not evidence that handles were released.
                    if (!child.HasExited)
                    {
                        try { child.Kill(); } catch (InvalidOperationException) { }
                        if (!child.WaitForExit(2000))
                            throw new InvalidOperationException("El lector XInput sigue activo; se cancela la reconexión del mando.");
                    }
                }
            }
        }
        private static async Task<string> Drain(StreamReader reader, int limit)
        {
            var value = new StringBuilder(); var buffer = new char[128]; int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                if (value.Length < limit) value.Append(buffer, 0, Math.Min(count, limit - value.Length));
                // Continue draining even if an unexpected child exceeds the bounded report.
            }
            return value.ToString();
        }
        internal static int RunWorker()
        {
            var value = new char[4];
            for (uint i = 0; i < 4; i++)
            {
                NativeState state; uint result = XInputGetState(i, out state);
                if (result != 0 && result != 1167) throw new Win32Exception((int)result);
                value[i] = result == 0 ? '1' : '0';
            }
            Console.Write(new string(value));
            return 0;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Pad
        { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeState { public uint Packet; public Pad Pad; }
        [DllImport("xinput1_4.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint XInputGetState(uint index, out NativeState state);
    }
}
