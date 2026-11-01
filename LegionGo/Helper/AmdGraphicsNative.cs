// SPDX-License-Identifier: AGPL-3.0-only
// Independently implemented runtime interop; no AMD SDK implementation or binary is bundled.
// ABI references and SDK redistribution restrictions: LegionGo/SOURCES.md, AMD ADLX section.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace LegionTweaks
{
    internal sealed class AmdGraphicsNative : IAmdGraphicsBackend
    {
        private IntPtr library, system, graphics;
        private End terminate;
        private bool initialized;
        private string version, topology;
        private int gpuCount, displayCount;
        private readonly List<IntPtr> references = new List<IntPtr>();
        private readonly Dictionary<string, Target> targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        private sealed class Target { internal IntPtr Pointer; internal int Read = 4, Write = 5; internal bool Integer; internal bool Range; internal bool ReadDormant; internal string Error; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Version(out ulong version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Start(ulong version, out IntPtr system);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int End();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Get(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFor(IntPtr self, IntPtr item, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Query(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Bool(IntPtr self, out byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Int(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SizeId(IntPtr self, out UIntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Range(IntPtr self, out IntRange value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetBool(IntPtr self, byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInt(IntPtr self, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Release(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Count(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int At(IntPtr self, uint index, out IntPtr value);
        [StructLayout(LayoutKind.Sequential)] private struct IntRange { internal int Min, Max, Step; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
        private T Export<T>(string name) where T : class
        {
            var pointer = GetProcAddress(library, name);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("Falta la entrada ADLX " + name + ".");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }
        private static T Method<T>(IntPtr obj, int slot) where T : class
        {
            if (obj == IntPtr.Zero) throw new InvalidOperationException("Interfaz ADLX ausente.");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T));
        }
        private static void Check(int result)
        { if (result != 0 && result != 1 && result != 2) throw new InvalidOperationException("ADLX devolvió código " + result + "."); }
        private IntPtr Keep(IntPtr value) { if (value != IntPtr.Zero) references.Add(value); return value; }
        private IntPtr GetObject(IntPtr parent, int slot)
        { IntPtr result; Check(Method<Get>(parent, slot)(parent, out result)); if (result == IntPtr.Zero) throw new InvalidOperationException("ADLX no devolvió la interfaz."); return Keep(result); }
        internal AmdGraphicsNative()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8)
                throw new InvalidOperationException("Se requiere Windows x64.");
            try {
                // Absolute system path, system-only dependency search; never load a game-local DLL.
                library = LoadLibraryEx(Path.Combine(Environment.SystemDirectory, "amdadlx64.dll"), IntPtr.Zero, 0x800);
                if (library == IntPtr.Zero) throw new InvalidOperationException("El controlador no ofrece una biblioteca ADLX x64 accesible.");
                ulong full; Check(Export<Version>("ADLXQueryFullVersion")(out full));
                version = (full >> 48) + "." + ((full >> 32) & 65535) + "." + ((full >> 16) & 65535) + "." + (full & 65535);
                terminate = Export<End>("ADLXTerminate");
                int started = Export<Start>("ADLXInitialize")(full, out system);
                // Never terminate an initialization owned by a different ADLX user in this process.
                if (started != 0 || system == IntPtr.Zero) throw new InvalidOperationException("No se pudo iniciar una sesión ADLX propia (" + started + ").");
                initialized = true;
                // IADLXSystem is not ref-counted: Get3DSettingsServices is slot 7, not IUnknown+7.
            }
            catch { Dispose(); throw; }
        }
        private void Discover()
        {
            ReleaseReferences(); targets.Clear();
            graphics = GetObject(system, 7);
            AddGlobal("rsr", graphics, 14);
            var rsr = targets["rsr"];
            targets["rsr_sharpness"] = new Target { Pointer = rsr.Pointer, Read = 7, Write = 8, Integer = true, Range = true, Error = rsr.Error };
            IntPtr graphics1;
            int query = Method<Query>(graphics, 2)(graphics, "IADLX3DSettingsServices1", out graphics1);
            if (query == 0 && graphics1 != IntPtr.Zero) { Keep(graphics1); AddGlobal("afmf", graphics1, 17); }
            else targets["afmf"] = new Target { Error = query == 6 || query == 12 ? null : "Interfaz AFMF no disponible (" + query + ")." };
            ReadTopology();
        }
        private void AddGlobal(string key, IntPtr parent, int slot)
        {
            IntPtr value;
            int result = Method<Get>(parent, slot)(parent, out value);
            targets[key] = new Target { Pointer = result == 0 ? Keep(value) : IntPtr.Zero, Error = result == 0 || result == 6 || result == 12 ? null : "Interfaz no disponible (" + result + ")." };
        }
        private void AddDependent(string key, IntPtr parent, int slot, IntPtr item, bool integer = false, int writeSlot = 5)
        {
            IntPtr value;
            int result = Method<GetFor>(parent, slot)(parent, item, out value);
            targets[key] = new Target { Pointer = result == 0 ? Keep(value) : IntPtr.Zero, Integer = integer, Write = writeSlot,
                Error = result == 0 || result == 6 || result == 12 ? null : "Dependencia no disponible (" + result + ")." };
        }
        private void ReadTopology()
        {
            var ids = new List<string>();
            var gpus = GetObject(system, 1); gpuCount = checked((int)Method<Count>(gpus, 3)(gpus));
            if (gpuCount > 8) throw new InvalidOperationException("Demasiadas GPU AMD.");
            for (uint i = 0; i < gpuCount; i++) {
                IntPtr gpu; Check(Method<At>(gpus, 11)(gpus, i, out gpu)); Keep(gpu);
                int id; Check(Method<Int>(gpu, 18)(gpu, out id)); string prefix = "gpu:" + id + ":"; ids.Add(prefix);
                AddDependent(prefix + "boost", graphics, 5, gpu, writeSlot: 7);
                AddDependent(prefix + "image_sharpening", graphics, 6, gpu, writeSlot: 7);
                AddDependent(prefix + "chill", graphics, 4, gpu, writeSlot: 8);
                AddDependent(prefix + "anti_lag", graphics, 3, gpu);
            }
            var displays = GetObject(system, 3); var list = GetObject(displays, 4);
            displayCount = checked((int)Method<Count>(list, 3)(list));
            if (displayCount > 16) throw new InvalidOperationException("Demasiadas pantallas AMD.");
            for (uint i = 0; i < displayCount; i++) {
                IntPtr display; Check(Method<At>(list, 11)(list, i, out display)); Keep(display);
                UIntPtr id; Check(Method<SizeId>(display, 13)(display, out id)); string prefix = "display:" + id.ToUInt64() + ":"; ids.Add(prefix);
                AddDependent(prefix + "gpu_scaling", displays, 11, display);
                AddDependent(prefix + "scaling_mode", displays, 12, display, true);
                AddDependent(prefix + "integer_scaling", displays, 13, display);
                targets[prefix + "integer_scaling"].ReadDormant = true;
            }
            topology = string.Join("|", ids.OrderBy(x => x, StringComparer.Ordinal));
        }
        public AmdGraphicsStatus Read()
        {
            if (!initialized) throw new ObjectDisposedException(nameof(AmdGraphicsNative));
            Discover(); // Topology and interfaces are refreshed; detached devices never reuse an old target.
            var values = new List<AmdGraphicsValue>();
            foreach (var pair in targets.OrderBy(x => x.Key, StringComparer.Ordinal)) {
                var value = new AmdGraphicsValue { Key = pair.Key }; var target = pair.Value;
                try {
                    if (target.Error != null) throw new InvalidOperationException(target.Error);
                    if (target.Pointer == IntPtr.Zero) value.Supported = false;
                    else {
                        byte supported; Check(Method<Bool>(target.Pointer, 3)(target.Pointer, out supported));
                        if (supported > 1) throw new InvalidOperationException("Indicador de soporte AMD inválido.");
                        value.Supported = supported != 0;
                        if (!value.Supported.Value && target.ReadDormant) {
                            // GPU scaling off makes integer scaling unsupported, but some drivers
                            // still expose its stored value. Preserve that value when it is readable.
                            byte dormant; int result = Method<Bool>(target.Pointer, target.Read)(target.Pointer, out dormant);
                            if (result == 0 && dormant <= 1) value.Value = dormant;
                        }
                        if (value.Supported == true) {
                            if (target.Integer) { int actual; Check(Method<Int>(target.Pointer, target.Read)(target.Pointer, out actual)); value.Value = actual; }
                            else { byte actual; Check(Method<Bool>(target.Pointer, target.Read)(target.Pointer, out actual));
                                if (actual > 1) throw new InvalidOperationException("Indicador AMD inválido."); value.Value = actual; }
                            if (target.Range) {
                                IntRange range; Check(Method<Range>(target.Pointer, 6)(target.Pointer, out range));
                                if (range.Step <= 0 || range.Min > range.Max) throw new InvalidOperationException("Rango AMD inválido.");
                                value.Minimum = range.Min; value.Maximum = range.Max; value.Step = range.Step;
                            }
                        }
                    }
                }
                catch (Exception ex) { value.Value = null; value.Error = ex.Message; }
                values.Add(value);
            }
            return new AmdGraphicsStatus { Available = true, RuntimeVersion = version, Topology = topology, GpuCount = gpuCount,
                DisplayCount = displayCount, Values = values.ToArray(), CapturedAtUtc = DateTime.UtcNow.ToString("o") };
        }
        public void Write(string key, int value)
        {
            Target target;
            if (!initialized || !targets.TryGetValue(key, out target) || target.Pointer == IntPtr.Zero || target.Error != null)
                throw new InvalidOperationException("Ajuste AMD no disponible.");
            if (target.Integer) Check(Method<SetInt>(target.Pointer, target.Write)(target.Pointer, value));
            else {
                if (value != 0 && value != 1) throw new ArgumentException("Valor booleano AMD inválido.");
                Check(Method<SetBool>(target.Pointer, target.Write)(target.Pointer, (byte)value));
            }
        }
        public void Dispose()
        {
            try { ReleaseReferences(); }
            finally {
                targets.Clear();
                try { if (initialized) { initialized = false; terminate(); } }
                finally { system = IntPtr.Zero; if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; } }
            }
        }
        private void ReleaseReferences()
        { for (int i = references.Count - 1; i >= 0; i--) Method<Release>(references[i], 1)(references[i]); references.Clear(); }
    }
}
