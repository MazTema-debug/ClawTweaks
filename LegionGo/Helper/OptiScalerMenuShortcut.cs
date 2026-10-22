// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LegionTweaks
{
    internal interface IOptiScalerMenuShortcut
    {
        bool Supported { get; }
        void OpenMenu();
    }
    internal struct OptiScalerWindow
    {
        internal bool Available, GameBar, Eligible;
        internal long Handle;
        internal int ProcessId;
        internal bool Same(OptiScalerWindow other) => Handle == other.Handle && ProcessId == other.ProcessId;
    }
    internal interface IOptiScalerMenuPort
    {
        OptiScalerWindow Inspect();
        void CloseGameBar();
        void SendInsert(OptiScalerWindow expected);
        void Wait(int milliseconds);
    }
    internal interface IOptiScalerKeyboardPort
    {
        OptiScalerWindow Inspect();
        bool IsKeyDown(int key);
        uint SendKeys(GameBarShortcut.Input[] keys);
    }

    internal static class OptiScalerInsert
    {
        internal static void Send(IOptiScalerKeyboardPort port, OptiScalerWindow expected, Func<long> milliseconds = null)
        {
            var clock = Stopwatch.StartNew();
            milliseconds = milliseconds ?? (() => clock.ElapsedMilliseconds);
            var current = port.Inspect();
            if (!expected.Available || !expected.Eligible || !current.Available || !current.Eligible || !current.Same(expected))
                throw new InvalidOperationException("Atajo cancelado: el juego perdió el foco.");
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0x2d })
                if (port.IsKeyDown(key))
                    throw new InvalidOperationException("Suelta Insert y las teclas modificadoras antes de volver a intentarlo.");
            var keys = new[] { NativeOptiScalerMenuPort.Insert(false), NativeOptiScalerMenuPort.Insert(true) };
            current = port.Inspect();
            if (!current.Available || !current.Eligible || !current.Same(expected) || milliseconds() > 100)
                throw new InvalidOperationException("Atajo cancelado: cambió el foco.");
            uint sent = port.SendKeys(keys);
            if (sent != 2)
            {
                int error = Marshal.GetLastWin32Error();
                // Release only our own accepted down event, never a user-held key.
                if (sent == 1) port.SendKeys(new[] { NativeOptiScalerMenuPort.Insert(true) });
                throw new Win32Exception(error, "Windows no confirmó el envío completo de Insert.");
            }
        }
    }

    // One explicit, fixed shortcut. The whole close/wait/send operation lives in
    // the helper because Game Bar may suspend its widget as soon as it closes.
    internal sealed class OptiScalerMenuShortcut : IOptiScalerMenuShortcut
    {
        private readonly IOptiScalerMenuPort port;
        public bool Supported { get; }
        internal OptiScalerMenuShortcut(bool supported, IOptiScalerMenuPort port = null)
        { Supported = supported; this.port = port ?? new NativeOptiScalerMenuPort(); }

        public void OpenMenu()
        {
            if (!Supported) throw new InvalidOperationException("El acceso a OptiScaler requiere una Legion Go Z1 Extreme compatible.");
            GameBarShortcut.WithKeyboardGate(OpenMenuCore);
        }
        private void OpenMenuCore()
        {
                var clock = Stopwatch.StartNew();
                var initial = port.Inspect();
                if (!initial.Available || !initial.GameBar || clock.ElapsedMilliseconds >= 2000)
                    throw new InvalidOperationException("Abre LegionTweaks desde Game Bar sobre el juego y vuelve a pulsar el acceso a OptiScaler.");
                port.CloseGameBar();
                OptiScalerWindow target = default(OptiScalerWindow);
                // At most two seconds. No retry, foreground stealing, key hook,
                // process launch or stored keyboard action survives this call.
                for (int step = 0, stable = 0; step < 80 && clock.ElapsedMilliseconds < 2000; step++)
                {
                    port.Wait(25);
                    var current = port.Inspect();
                    if (!current.Available) throw new InvalidOperationException("Atajo cancelado: Windows no está disponible para recibir entrada.");
                    if (current.GameBar)
                    {
                        if (target.Handle != 0) throw new InvalidOperationException("Atajo cancelado: cambió el foco antes de enviar Insert.");
                        continue;
                    }
                    if (!current.Eligible) throw new InvalidOperationException("Atajo cancelado: activa el juego antes de abrir Game Bar.");
                    if (target.Handle == 0) target = current;
                    else if (!target.Same(current)) throw new InvalidOperationException("Atajo cancelado: cambió la ventana de destino.");
                    if (++stable < 6) continue;
                    if (clock.ElapsedMilliseconds >= 2000) break;
                    port.SendInsert(target);
                    return;
                }
                throw new TimeoutException("Game Bar no devolvió el foco a tiempo; no se envió Insert.");
        }
    }

    internal sealed class NativeOptiScalerMenuPort : IOptiScalerMenuPort, IOptiScalerKeyboardPort
    {
        public OptiScalerWindow Inspect()
        {
            var desktop = new MouseOutput().Inspect();
            if (!desktop.Available) return default(OptiScalerWindow);
            uint pid;
            GetWindowThreadProcessId(new IntPtr(desktop.Foreground), out pid);
            if (pid == 0 || pid > int.MaxValue) return default(OptiScalerWindow);
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    string name = process.ProcessName;
                    bool bar = name.Equals("GameBar", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("GameBarFTServer", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("LegionTweaks.Widget", StringComparison.OrdinalIgnoreCase);
                    bool blocked = name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("LockApp", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("LogonUI", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("consent", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("LegionTweaks.Helper", StringComparison.OrdinalIgnoreCase);
                    return new OptiScalerWindow { Available = true, GameBar = bar, Eligible = !bar && !blocked,
                        Handle = desktop.Foreground, ProcessId = (int)pid };
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
            { return default(OptiScalerWindow); }
        }
        public void CloseGameBar()
        {
            var current = Inspect();
            if (!current.Available || !current.GameBar || (GetAsyncKeyState(0x2d) & 0x8000) != 0)
                throw new InvalidOperationException("Atajo cancelado: abre Game Bar y suelta Insert.");
            GameBarShortcut.Toggle();
        }
        public void Wait(int milliseconds) => Thread.Sleep(milliseconds);
        public void SendInsert(OptiScalerWindow expected) => OptiScalerInsert.Send(this, expected);
        public bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        public uint SendKeys(GameBarShortcut.Input[] keys) => SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(GameBarShortcut.Input)));
        // Extended-key flag distinguishes Insert from keypad 0.
        internal static GameBarShortcut.Input Insert(bool up) => new GameBarShortcut.Input { Type = 1, Key = 0x2d, Flags = up ? 3u : 1u };
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint SendInput(uint count, GameBarShortcut.Input[] inputs, int size);
    }
}
