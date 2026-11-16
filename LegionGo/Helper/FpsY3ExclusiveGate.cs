// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    // A reusable filesystem exclusion primitive, not a maintenance permission.
    // Production activation and maintenance use the protected adapter; legacy
    // helpers do not know this gate, and there is no direct gate CLI.
    internal sealed class FpsY3ExclusiveGate : IFpsY3AdmissionLease
    {
        internal const string Leaf = "fps-y3-admission-v1.guard";
        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, Delete = 0x00010000;
        private const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
        private const uint CreateNew = 1, OpenExisting = 3;
        private const uint Normal = 0x80, OpenReparsePoint = 0x00200000, WriteThrough = 0x80000000;
        private const int FileDispositionInfo = 4;
        private readonly object sync = new object();
        private readonly string path, identity;
        private readonly byte[] marker;
        private readonly Action requireProtectedDirectory;
        private FileStream stream;

        private FpsY3ExclusiveGate(string path, string identity, byte[] marker,
            Action requireProtectedDirectory, FileStream stream)
        {
            this.path = path; this.identity = identity; this.marker = marker;
            this.requireProtectedDirectory = requireProtectedDirectory; this.stream = stream;
        }

        // The file is created once, atomically, in a caller-verified protected
        // directory. If this process dies, the file remains and future callers
        // fail closed. No stale marker is ever removed automatically.
        internal static FpsY3ExclusiveGate Acquire(string directory, string role, Action requireProtectedDirectory) =>
            Acquire(directory, role, requireProtectedDirectory == null ? (Action<bool>)null : required => requireProtectedDirectory());

        // Production supplies a check which distinguishes the initial absent
        // leaf from the required, protected marker after successful creation.
        internal static FpsY3ExclusiveGate Acquire(string directory, string role, Action<bool> requireProtectedDirectory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)
                || (role != "activation" && role != "maintenance") || requireProtectedDirectory == null)
                throw new ArgumentException("Solicitud de exclusión Y3 inválida.");
            requireProtectedDirectory(false);
            string path = Path.Combine(Path.GetFullPath(directory), Leaf);
            SafeFileHandle handle = Open(path, GenericRead | GenericWrite | Delete, ShareRead, CreateNew, Normal | OpenReparsePoint | WriteThrough);
            if (handle.IsInvalid) {
                int error = Marshal.GetLastWin32Error(); handle.Dispose();
                if (error == 80 || error == 183 || error == 32 || error == 5)
                    throw new IOException("La exclusión Y3 está ocupada o requiere revisión.");
                throw new Win32Exception(error);
            }
            FileStream stream = null;
            try {
                stream = new FileStream(handle, FileAccess.ReadWrite, 4096, false); handle = null;
                byte[] marker = Encoding.ASCII.GetBytes("LegionTweaks.Y3.Gate.v1\n" + role + "\n" + Guid.NewGuid().ToString("N") + "\n");
                stream.Write(marker, 0, marker.Length); stream.Flush(true);
                string identity = FpsY3JournalInspector.NativeFileIdentity(stream);
                requireProtectedDirectory(true);
                return new FpsY3ExclusiveGate(path, identity, marker, () => requireProtectedDirectory(true), stream);
            }
            catch { if (stream != null) stream.Dispose(); else if (handle != null) handle.Dispose(); throw; }
        }

        // The caller invokes this ONLY after its own transaction has reached a
        // verified safe boundary. Mark deletion on the open file handle, not by
        // name after releasing it. Failed checks leave the durable marker intact.
        public void Complete()
        {
            lock (sync) {
                if (stream == null) throw new InvalidOperationException("La exclusión Y3 ya no está abierta.");
                requireProtectedDirectory();
                VerifyStream(stream);
                using (SafeFileHandle other = Open(path, GenericRead, ShareRead | ShareWrite | ShareDelete, OpenExisting, Normal | OpenReparsePoint)) {
                    if (other.IsInvalid) throw new IOException("No se pudo confirmar la identidad de la exclusión Y3.");
                    using (var check = new FileStream(other, FileAccess.Read, 4096, false)) VerifyStream(check);
                }
                requireProtectedDirectory();
                var disposition = new Disposition { DeleteFile = 1 };
                if (!SetFileInformationByHandle(stream.SafeFileHandle, FileDispositionInfo, ref disposition, Marshal.SizeOf(typeof(Disposition))))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var prior = stream; stream = null; prior.Dispose();
            }
        }

        private void VerifyStream(FileStream value)
        {
            if (FpsY3JournalInspector.NativeFileIdentity(value) != identity || value.Length != marker.Length)
                throw new IOException("La identidad de la exclusión Y3 cambió.");
            value.Position = 0;
            for (int i = 0; i < marker.Length; i++) if (value.ReadByte() != marker[i])
                throw new IOException("La exclusión Y3 cambió.");
            if (value.ReadByte() != -1) throw new IOException("La exclusión Y3 cambió.");
        }

        // Disposing without an explicit verified Complete never clears the file.
        public void Dispose()
        {
            lock (sync) { var prior = stream; stream = null; if (prior != null) prior.Dispose(); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Disposition { internal byte DeleteFile; }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle Open(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        private static SafeFileHandle Open(string path, uint access, uint share, uint creation, uint flags) =>
            Open(path, access, share, IntPtr.Zero, creation, flags, IntPtr.Zero);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass,
            ref Disposition information, int size);
    }
}
