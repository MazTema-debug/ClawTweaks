// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    // Protected recovery data; native mode bytes are never accepted through IPC.
    [DataContract]
    internal sealed class DisplaySnapshot
    {
        [DataMember] public DisplayInfo Info { get; set; }
        [DataMember] public string Identity { get; set; }
        [DataMember] public byte[] NativeMode { get; set; }
        internal bool SameOriginal(DisplaySnapshot other) => SameDevice(other) && Info.Current.Same(other.Info.Current);
        internal bool SameDevice(DisplaySnapshot other) => other?.Info != null && !string.IsNullOrEmpty(Identity)
            && Identity == other.Identity && Info.DeviceName == other.Info.DeviceName && Info.Orientation == other.Info.Orientation;
    }
    [DataContract]
    internal sealed class DisplayPlan
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public string Token { get; set; }
        [DataMember] public int OwnerPid { get; set; }
        [DataMember] public long OwnerStarted { get; set; }
        [DataMember] public long CreatedUtc { get; set; }
        [DataMember] public DisplaySnapshot Original { get; set; }
        [DataMember] public DisplayMode Target { get; set; }
    }
    [DataContract]
    internal sealed class DisplayTrialResult
    {
        [DataMember] public string Token { get; set; }
        [DataMember] public string Phase { get; set; }
        [DataMember] public bool Pending { get; set; }
        [DataMember] public long DeadlineUtc { get; set; }
        [DataMember] public int WorkerPid { get; set; }
        [DataMember] public long WorkerStarted { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string Error { get; set; }
    }
    internal interface IDisplayBackend
    {
        DisplaySnapshot Query();
        void Test(DisplaySnapshot original, DisplayMode target);
        void Apply(DisplaySnapshot original, DisplayMode target);
        void Restore(DisplaySnapshot original, DisplayMode expectedTarget);
    }
    internal interface IDisplayTrialPort
    {
        long ElapsedMilliseconds { get; }
        long UtcTicks { get; }
        bool OwnerAlive { get; }
        bool Interactive { get; }
        string Decision { get; }
        void Wait();
        void Save(DisplayTrialResult result);
    }
    internal static class DisplayTrialRules
    {
        internal const int ConfirmationMilliseconds = 15000;
        internal static void ValidatePlan(DisplayPlan plan)
        {
            Guid id;
            if (plan == null || plan.Version != 1 || !Guid.TryParseExact(plan.Token, "N", out id)
                || plan.OwnerPid <= 0 || plan.OwnerStarted <= 0 || plan.CreatedUtc <= 0 || plan.Original?.Info?.Current == null
                || string.IsNullOrWhiteSpace(plan.Original.Identity) || plan.Original.NativeMode == null
                || plan.Original.NativeMode.Length != 220 || plan.Target == null || plan.Target.Width < 320 || plan.Target.Width > 16384
                || plan.Target.Height < 320 || plan.Target.Height > 16384 || plan.Target.Frequency < 2 || plan.Target.Frequency > 1000)
                throw new InvalidOperationException("El respaldo de pantalla es inválido; se conserva para revisión.");
        }
        internal static DisplayTrialResult Run(DisplayPlan plan, IDisplayBackend backend, IDisplayTrialPort port, bool recoveryOnly = false)
        {
            ValidatePlan(plan);
            var result = new DisplayTrialResult { Token = plan.Token, Pending = true, Phase = "Preparing" };
            bool attempted = recoveryOnly;
            try
            {
                if (recoveryOnly) return Rollback(plan, backend, port, result);
                var age = port.UtcTicks - plan.CreatedUtc;
                if (age < 0 || age > TimeSpan.FromSeconds(30).Ticks || !port.OwnerAlive || !port.Interactive || port.Decision != null)
                    return Finish(port, result, "Canceled", "Prueba cancelada antes de cambiar la pantalla.");
                var fresh = backend.Query();
                if (!plan.Original.SameOriginal(fresh) || !fresh.Info.CanChange || plan.Original.Info.Current.Same(plan.Target)
                    || !(fresh.Info.Modes ?? new DisplayMode[0]).Any(x => x.Same(plan.Target)))
                    throw new InvalidOperationException("Cambió la pantalla o el modo dejó de estar disponible. Consulta los modos de nuevo.");
                backend.Test(plan.Original, plan.Target);
                if (!port.OwnerAlive || !port.Interactive || port.Decision != null)
                    return Finish(port, result, "Canceled", "Prueba cancelada antes de cambiar la pantalla.");
                long start = port.ElapsedMilliseconds;
                result.DeadlineUtc = port.UtcTicks + TimeSpan.FromMilliseconds(ConfirmationMilliseconds).Ticks;
                result.Phase = "Applying"; result.Message = "Aplicando el modo provisional."; port.Save(result);
                attempted = true; // A failing native call may have changed part of the state.
                backend.Apply(plan.Original, plan.Target);
                if (!IsTarget(plan, backend.Query())) throw new InvalidOperationException("Windows no confirmó el modo solicitado.");
                result.Phase = "AwaitingConfirmation"; result.Message = "Confirma el modo antes de 15 segundos o se intentará volver al anterior."; port.Save(result);
                while (port.ElapsedMilliseconds - start < ConfirmationMilliseconds && port.UtcTicks < result.DeadlineUtc
                    && port.OwnerAlive && port.Interactive)
                {
                    string decision = port.Decision;
                    if (decision == "revert") break;
                    if (decision == "confirm")
                    {
                        if (!IsTarget(plan, backend.Query())) throw new InvalidOperationException("Cambió el modo antes de confirmar.");
                        // Recheck after the potentially slow driver query.
                        if (port.ElapsedMilliseconds - start >= ConfirmationMilliseconds || port.UtcTicks >= result.DeadlineUtc
                            || !port.OwnerAlive || !port.Interactive) break;
                        return Finish(port, result, "Confirmed", "Modo confirmado para esta sesión de Windows. No se guardó para el reinicio.");
                    }
                    if (decision != null) throw new InvalidOperationException("Decisión de pantalla inválida.");
                    port.Wait();
                }
                return Rollback(plan, backend, port, result);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                if (attempted) return Rollback(plan, backend, port, result);
                return Finish(port, result, "Failed", "No se cambió la pantalla.");
            }
        }
        private static bool IsTarget(DisplayPlan plan, DisplaySnapshot current) => plan.Original.SameDevice(current) && plan.Target.Same(current.Info.Current);
        private static DisplayTrialResult Rollback(DisplayPlan plan, IDisplayBackend backend, IDisplayTrialPort port, DisplayTrialResult result)
        {
            result.Pending = true;
            try
            {
                var current = backend.Query();
                if (plan.Original.SameOriginal(current)) return Finish(port, result, "Reverted", "Modo anterior verificado.");
                if (!IsTarget(plan, current)) throw new InvalidOperationException("La pantalla o su modo ya no coinciden con la prueba; no se sobrescribió el estado actual.");
                if (!port.Interactive) throw new InvalidOperationException("Desbloquea Windows y pulsa Recuperar modo anterior.");
                result.Phase = "Reverting"; result.Message = "Recuperando el modo anterior."; port.Save(result);
                backend.Restore(plan.Original, plan.Target);
                if (!plan.Original.SameOriginal(backend.Query())) throw new InvalidOperationException("Windows no confirmó el modo anterior.");
                return Finish(port, result, "Reverted", "Modo anterior restaurado y verificado.");
            }
            catch (Exception ex)
            {
                result.Phase = "RecoveryPending"; result.Pending = true;
                result.Message = "Recuperación de pantalla pendiente. Se conserva el respaldo.";
                result.Error = (result.Error == null ? "" : result.Error + " · ") + ex.Message;
                port.Save(result); return result;
            }
        }
        private static DisplayTrialResult Finish(IDisplayTrialPort port, DisplayTrialResult result, string phase, string message)
        {
            result.Pending = false; result.Phase = phase; result.Message = message;
            port.Save(result); return result;
        }
    }
}
