// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    internal sealed class FpsY3NativeOriginal
    {
        [DataMember] internal string Path, Container;
        [DataMember] internal byte[] Usb3, Usb4, Map3, Map4;

        internal void Validate()
        {
            Guid container;
            if (Path == null || Path.Length > 1024 || !Path.StartsWith(@"\\?\hid#vid_17ef&pid_61ee&mi_02#", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Container, "D", out container) || container == Guid.Empty)
                throw new InvalidOperationException("Identidad FPS original inválida.");
            foreach (var usb in new[] { Usb3, Usb4 }) {
                FpsY3NativeTransport.ValidateEnvelope(usb, 0x79, 1);
                if (usb[1] != 21 || FpsY3NativeTransport.BigEndian(usb, 17) != 0x101a0)
                    throw new InvalidOperationException("La prueba requiere el protocolo FPS original verificado.");
            }
            if (Usb3[4] != 3 || Usb4[4] != 4) throw new InvalidOperationException("Selectores originales inválidos.");
            FpsY3NativeTransport.DecodeMap(Map3, 3);
            var right = FpsY3NativeTransport.DecodeMap(Map4, 4);
            if (!right.ContainsKey(30) || !right[30].SequenceEqual(new byte[] { 6 })
                || !right.ContainsKey(34) || !right[34].SequenceEqual(new byte[] { 5 }))
                throw new InvalidOperationException("La prueba requiere Y3=X2 y M3=X1 originales. No se cambiará una asignación personalizada no verificada.");
        }
        internal static FpsY3NativeOriginal Capture(string expectedPath)
        {
            FpsY3NativeTransport.RequireOriginalGo();
            var b = new FpsY3NativeOriginal { Path = FpsY3NativeTransport.FindInterface() };
            if (!string.Equals(expectedPath, b.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("El lector físico y el destino de escritura no coinciden.");
            b.Container = FpsY3NativeTransport.ContainerForPath(b.Path);
            using (var h = FpsY3NativeTransport.Open(b.Path)) {
                FpsY3NativeTransport.ReadIndex(h);
                b.Usb3 = FpsY3NativeTransport.ReadUsb(h, 3); b.Usb4 = FpsY3NativeTransport.ReadUsb(h, 4);
                b.Map3 = FpsY3NativeTransport.ReadMap(h, 3); b.Map4 = FpsY3NativeTransport.ReadMap(h, 4);
                FpsY3NativeTransport.ReadIndex(h);
            }
            b.Validate(); return b;
        }
        internal void Execute(string operation, Action beforeWrite)
        {
            Validate();
            if (FpsY3NativeTransport.ContainerForPath(Path) != Container) throw new InvalidOperationException("Cambió el contenedor físico FPS.");
            using (var h = FpsY3NativeTransport.Open(Path)) {
                Action confirm = () => {
                    FpsY3NativeTransport.ReadIndex(h);
                    var u3 = FpsY3NativeTransport.ReadUsb(h, 3); var u4 = FpsY3NativeTransport.ReadUsb(h, 4);
                    if (!u3.Take(22).SequenceEqual(Usb3.Take(22)) || !u4.Take(22).SequenceEqual(Usb4.Take(22)))
                        throw new InvalidOperationException("Cambió la identificación del dispositivo FPS.");
                };
                confirm();
                if (operation == "mute") {
                    FpsY3NativeTransport.Match(Map3, FpsY3NativeTransport.ReadMap(h, 3), 3, 6);
                    FpsY3NativeTransport.Match(Map4, FpsY3NativeTransport.ReadMap(h, 4), 4, 6);
                    beforeWrite(); FpsY3NativeTransport.WriteY3(h, 0);
                }
                else if (operation == "restore") {
                    bool needed = true;
                    try { needed = !FpsY3NativeTransport.DecodeMap(FpsY3NativeTransport.ReadMap(h, 4), 4)[30].SequenceEqual(new byte[] { 6 }); }
                    catch (Exception) { }
                    if (needed) { confirm(); beforeWrite(); try { FpsY3NativeTransport.WriteY3(h, 6); } catch (Exception) { /* Readback settles an uncertain ACK. */ } }
                }
                else if (operation != "verify") throw new ArgumentException("Operación FPS inválida.");
                confirm(); int expected = operation == "restore" ? 6 : 0;
                FpsY3NativeTransport.Match(Map3, FpsY3NativeTransport.ReadMap(h, 3), 3, expected);
                FpsY3NativeTransport.Match(Map4, FpsY3NativeTransport.ReadMap(h, 4), 4, expected);
            }
        }
    }
}
