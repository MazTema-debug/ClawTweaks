// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Threading;

namespace LegionTweaks
{
    // Holds handles, not mutex ownership. The .49 principal checks createdNew,
    // so an existing named object rejects a new principal. It does NOT stop
    // legacy workers, prove SID coverage, authorize mutation or survive a crash.
    // This is not exposed as a helper command or connected to an installer yet.
    public sealed class FpsY3LegacyInstanceFence : IDisposable
    {
        private readonly object sync = new object();
        private List<Mutex> handles;
        private FpsY3LegacyInstanceFence(List<Mutex> handles) { this.handles = handles; }

        // The caller must first establish the complete set of relevant SIDs and
        // their identities. Sessions of one SID share the legacy object name.
        public static FpsY3LegacyInstanceFence AcquireVerifiedSids(IEnumerable<string> verifiedSids)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("La exclusión legada requiere una identidad elevada.");
            return AcquireVerifiedSids(verifiedSids, sid => new Mutex(false, @"Global\LegionTweaks.Z1E." + sid));
        }

        public int SidCount { get { lock (sync) return handles == null ? 0 : handles.Count; } }

        // An injectable opener permits tests to use only unique Local fixtures.
        internal static FpsY3LegacyInstanceFence AcquireVerifiedSids(IEnumerable<string> verifiedSids, Func<string, Mutex> open)
        {
            if (verifiedSids == null || open == null) throw new ArgumentException("Identidades de mantenimiento inválidas.");
            var unique = new HashSet<string>(StringComparer.Ordinal); var sids = new List<string>();
            foreach (string sid in verifiedSids) {
                if (sid == null || sid.Length > 184 || sids.Count >= 64) throw new ArgumentException("Identidades de mantenimiento inválidas.");
                string canonical = new SecurityIdentifier(sid).Value;
                if (!string.Equals(sid, canonical, StringComparison.Ordinal) || !unique.Add(canonical))
                    throw new ArgumentException("Identidades de mantenimiento duplicadas o inválidas.");
                sids.Add(canonical);
            }
            if (sids.Count == 0) throw new ArgumentException("Faltan identidades de mantenimiento.");
            var handles = new List<Mutex>();
            try {
                foreach (string sid in sids) {
                    var handle = open(sid);
                    if (handle == null) throw new InvalidOperationException("No se pudo asegurar una instancia legada.");
                    handles.Add(handle);
                }
                return new FpsY3LegacyInstanceFence(handles);
            }
            catch (Exception failure) {
                var errors = new List<Exception> { failure };
                foreach (var handle in handles) try { handle.Dispose(); } catch (Exception ex) { errors.Add(ex); }
                if (errors.Count > 1) throw new AggregateException("No se confirmó la liberación de todas las instancias legadas.", errors);
                throw;
            }
        }

        public void Dispose()
        {
            lock (sync) {
                var prior = handles; handles = null;
                if (prior != null) {
                    var errors = new List<Exception>();
                    foreach (var handle in prior) try { handle.Dispose(); } catch (Exception ex) { errors.Add(ex); }
                    if (errors.Count != 0) throw new AggregateException("No se confirmó la liberación de todas las instancias legadas.", errors);
                }
            }
        }
    }
}
