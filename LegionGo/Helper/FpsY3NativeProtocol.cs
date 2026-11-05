// SPDX-License-Identifier: AGPL-3.0-only
// Bounded single-row native route. No Lenovo/HC executable code is loaded.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
internal static partial class FpsY3NativeTransport
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PropertyKey { internal Guid Format; internal uint Id; }
    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling=true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling=true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type, byte[] data, ref uint length, uint flags);

    internal static string ContainerForPath(string path)
    {
        if (path == null || !path.StartsWith(@"\\?\", StringComparison.Ordinal)) throw new InvalidOperationException("Ruta HID no reconocida.");
        string[] parts = path.Substring(4).Split('#');
        if (parts.Length != 4 || !parts[0].Equals("hid",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Ruta HID no reconocida.");
        string instance = parts[0]+"\\"+parts[1]+"\\"+parts[2];
        uint node;
        if (CM_Locate_DevNodeW(out node,instance,0) != 0) throw new InvalidOperationException("No se pudo identificar el nodo HID.");
        var key = new PropertyKey { Format=new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id=2 };
        var data = new byte[16]; uint type, length=16;
        if (CM_Get_DevNode_PropertyW(node,ref key,out type,data,ref length,0) != 0 || type != 13 || length != 16)
            throw new InvalidOperationException("Contenedor PnP no confirmado.");
        Guid container = new Guid(data);
        if (container == Guid.Empty) throw new InvalidOperationException("Contenedor vacío.");
        return container.ToString("D");
    }

    internal static SafeFileHandle Open(string expectedPath)
    {
        RequireOriginalGo();
        string path = FindInterface();
        if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cambió la identidad de la colección física.");
        var handle = CreateFile(path, ReadAccess | WriteAccess, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new System.ComponentModel.Win32Exception(); }
        return handle;
    }

    internal static byte[] FixedReport(byte command, byte subcommand, params byte[] body)
    {
        var result = new byte[64];
        result[0] = 5; result[1] = (byte)(body.Length + 4); result[2] = command; result[3] = subcommand;
        Array.Copy(body, 0, result, 4, body.Length); result[result[1]] = 1;
        return result;
    }

    internal static byte[] Y3Report(int key)
    {
        if (key != 6 && key != 0) throw new ArgumentException("Solo original 6 o candidato sin acción 0.");
        // OEM SetFPSKeyMap RVA 0x20d33..0x20ddc: selector, row count,
        // row sequence, FPS index, button, destination count, destinations.
        return FixedReport(0x6c, 0x0a, 4, 1, 1, 1, 30, 1, (byte)key);
    }

    private static bool Allowed(byte[] request)
    {
        if (request == null || request.Length != 64) return false;
        var allowed = new[] { IndexQuery(), MapQuery(3,1), MapQuery(4,1),
            FixedReport(0x79,1,3), FixedReport(0x79,1,4), Y3Report(6), Y3Report(0) };
        return allowed.Any(a => a.SequenceEqual(request));
    }

    private static byte[] Exchange(SafeFileHandle handle, byte[] request)
    {
        if (!Allowed(request)) throw new InvalidOperationException("Informe fuera de la lista cerrada del ensayo.");
        var reply = ExchangeUnchecked(handle, request);
        Console.WriteLine("RESP " + BitConverter.ToString(reply));
        return reply;
    }

    internal static int ReadIndex(SafeFileHandle handle)
    {
        byte[] reply = Exchange(handle, IndexQuery());
        ValidateEnvelope(reply, 0x6e, 1);
        int index;
        if (reply[1] != 6 || !TryIndex(reply, out index) || index != 1)
            throw new InvalidOperationException("Este ensayo exige índice FPS 1, sin cambiarlo.");
        return index;
    }

    internal static byte[] ReadUsb(SafeFileHandle handle, int selector)
    {
        if (selector != 3 && selector != 4) throw new ArgumentException("Selector inválido.");
        var reply = Exchange(handle, FixedReport(0x79, 1, (byte)selector));
        ValidateEnvelope(reply, 0x79, 1);
        if (reply[4] != selector || reply[1] != 21) throw new InvalidOperationException("USBVerify no reconocido.");
        // getUSBVerify RVA 0x16cba..0x16d42: four big-endian uint32,
        // beginning at full-report byte 5. The setter compares the fourth
        // value, struct +0x0c (stack +0xcc relative to stack +0xc0).
        uint protocol = BigEndian(reply, 17);
        Console.WriteLine("USB_SELECTOR=" + selector + " PROTOCOL=" + protocol.ToString("X8"));
        if (protocol != 0x101a0 && protocol != 0x200a0)
            throw new InvalidOperationException("Revisión de protocolo USB fuera de las dos ramas identificadas.");
        return reply;
    }

    internal static uint BigEndian(byte[] b, int i)
    {
        return ((uint)b[i] << 24) | ((uint)b[i+1] << 16) | ((uint)b[i+2] << 8) | b[i+3];
    }

    internal static byte[] ReadMap(SafeFileHandle handle, int selector)
    {
        var reply = Exchange(handle, MapQuery(selector, 1));
        DecodeMap(reply, selector);
        return reply;
    }

    internal static Dictionary<int, byte[]> DecodeMap(byte[] reply, int selector)
    {
        if (selector != 3 && selector != 4) throw new ArgumentException("Selector FPS inválido.");
        ValidateEnvelope(reply, 0x6c, 0x0b);
        if (reply[4] != selector || reply[5] != 1 || reply[6] != 1 || reply[1] < 8)
            throw new InvalidOperationException("Selector, índice o campo de respuesta inesperados.");
        int cursor = 8;
        var rows = new Dictionary<int, byte[]>();
        for (int row = 0; row < reply[7]; row++)
        {
            if (cursor + 2 > reply[1]) throw new InvalidOperationException("Fila truncada.");
            int button = reply[cursor++], count = reply[cursor++];
            if (count < 1 || cursor + count > reply[1] || rows.ContainsKey(button))
                throw new InvalidOperationException("Destinos truncados o botón repetido.");
            rows.Add(button, reply.Skip(cursor).Take(count).ToArray()); cursor += count;
        }
        if (cursor != reply[1] || rows.Count != (selector == 3 ? 15 : 16))
            throw new InvalidOperationException("El mapa difiere del alcance de la línea base.");
        return rows;
    }

    internal static void ValidateEnvelope(byte[] reply, int command, int subcommand)
    {
        if (reply == null || reply.Length != 64 || reply[0] != 4 || reply[1] < 5 || reply[1] > 63
            || reply[2] != command || reply[3] != subcommand || reply[reply[1]] != 1)
            throw new InvalidOperationException("Respuesta HID inválida.");
        for (int i = reply[1]+1; i < 64; i++)
        {
            // USBVerify has a varying byte after its declared terminator
            // (observed 0x80/0x05). The OEM getter ignores that byte. Preserve
            // it but do not interpret it as body data or part of identity.
            if (command == 0x79 && subcommand == 1 && reply[1] == 21 && i == 22) continue;
            if (reply[i] != 0) throw new InvalidOperationException("Relleno de respuesta inesperado.");
        }
    }

    internal static void WriteY3(SafeFileHandle handle, int key)
    {
        byte[] reply = Exchange(handle, Y3Report(key));
        ValidateY3Ack(reply);
    }

    internal static void ValidateY3Ack(byte[] reply)
    {
        ValidateEnvelope(reply, 0x6c, 0x0a);
        // OEM ACK parser RVA 0x8f90: echoes total-row count and sequence,
        // then returns a result. The positive branch at 0x215e2 continues
        // the row loop (0x21689), rather than returning an error. Actual
        // Q and restore writes both returned 1, confirmed by map readback.
        // Accept only that observed response; still require map/index reads.
        if (reply[1] != 8 || reply[4] != 4 || reply[5] != 1 || reply[6] != 1 || reply[7] != 1)
            throw new InvalidOperationException("ACK de Y3 no confirmado; se requiere recuperación por lectura.");
    }

    internal static void Match(byte[] before, byte[] after, int selector, int expectedY3)
    {
        var original = DecodeMap(before, selector); var current = DecodeMap(after, selector);
        if (original.Count != current.Count || original.Keys.Any(k => !current.ContainsKey(k)))
            throw new InvalidOperationException("Cambió el conjunto de botones.");
        foreach (var row in original)
        {
            byte[] expected = selector == 4 && row.Key == 30 ? new[] { (byte)expectedY3 } : row.Value;
            if (!expected.SequenceEqual(current[row.Key]))
                throw new InvalidOperationException("Lectura distinta de la esperada en botón " + row.Key + ".");
        }
    }

    internal static void CheckProtocol()
    {
        CheckDecoder();
        if (BitConverter.ToString(Y3Report(0),0,12) != "05-0B-6C-0A-04-01-01-01-1E-01-00-01"
            || BitConverter.ToString(Y3Report(6),0,12) != "05-0B-6C-0A-04-01-01-01-1E-01-06-01")
            throw new Exception("Constructor de una fila inesperado.");
        var sample = Y3Report(0);
        for (int offset = 0; offset < 64; offset++)
        {
            var bad = (byte[])sample.Clone(); bad[offset] ^= 0x80;
            if (Allowed(bad)) throw new Exception("Mutación de salida aceptada.");
        }
        if (!Allowed(Y3Report(6)) || !Allowed(FixedReport(0x79,1,4)) || Allowed(FixedReport(0x6e,2,4,1)))
            throw new Exception("Lista de salidas incorrecta.");
        bool rejected = false; try { Y3Report(27); } catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new Exception("Destino ajeno aceptado.");
    }
}

}
