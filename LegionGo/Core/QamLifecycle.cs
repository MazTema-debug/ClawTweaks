// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class QamPreferences
    {
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public bool StartWithLegionTweaks { get; set; }
        [DataMember(IsRequired = true)] public LegionShortcutMode ToggleMode { get; set; } = LegionShortcutMode.Left;
        [DataMember(IsRequired = true)] public LegionShortcutMode DesktopToggleMode { get; set; }
        public void Validate()
        {
            if (Version != 1 || !QamLifecycleRules.IsMode(ToggleMode) || !QamLifecycleRules.IsMode(DesktopToggleMode)
                || QamLifecycleRules.Conflicts(ToggleMode, DesktopToggleMode)) throw new InvalidOperationException("La configuración de los menús no es compatible.");
        }
    }
    [DataContract]
    public sealed class QamLifecycleStatus
    {
        [DataMember] public bool Supported { get; set; }
        [DataMember] public bool PreferenceKnown { get; set; }
        [DataMember] public bool StartWithLegionTweaks { get; set; }
        [DataMember] public LegionShortcutMode ToggleMode { get; set; }
        [DataMember] public LegionShortcutMode EffectiveToggleMode { get; set; }
        [DataMember] public LegionShortcutMode DesktopToggleMode { get; set; }
        [DataMember] public LegionShortcutMode EffectiveDesktopToggleMode { get; set; }
        [DataMember] public bool DesktopRegistered { get; set; }
        [DataMember] public int SessionId { get; set; }
        [DataMember] public bool Running { get; set; }
        [DataMember] public bool Busy { get; set; }
        [DataMember] public bool Visible { get; set; }
        [DataMember] public int ProcessId { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string Error { get; set; }
    }
    public interface IQamLifecycle : IDisposable
    {
        QamLifecycleStatus GetStatus();
        QamLifecycleStatus Open();
        QamLifecycleStatus Configure(bool startup, LegionShortcutMode mode, LegionShortcutMode desktop);
        void RegisterDesktop(DesktopViewRegistration view, int clientPid);
        void RunAtStartup();
        void Suspend();
        void Resume();
    }
    public interface IQamShortcutControl
    {
        LegionShortcutMode QamShortcut { get; }
        LegionShortcutMode DesktopShortcut { get; }
        void SetMenuActions(Action qam, Action desktop);
        void ConfigureMenuShortcuts(LegionShortcutMode qam, LegionShortcutMode desktop, Action persist);
    }
    public static class QamLifecycleRules
    {
        public static bool IsMode(LegionShortcutMode mode) => mode >= LegionShortcutMode.Disabled && mode <= LegionShortcutMode.Both;
        public static bool Conflicts(LegionShortcutMode first, LegionShortcutMode second) => first != LegionShortcutMode.Disabled && first == second;
        public static bool SameInteractiveSession(int owner, int caller) => owner > 0 && owner == caller;
        public static void Validate(Request request)
        {
            bool configure = request?.Operation == "qam_configure";
            if (request == null || request.Version != Protocol.Version
                || (!configure && request.Operation != "qam_status" && request.Operation != "qam_open" && request.Operation != "qam_desktop_register")
                || request.Key != null || request.Ac || request.Profile != null || request.Display != null
                || request.RtssAppearance != null || request.GameBarInput != null || request.Remap != null || request.AmdGraphics != null
                || (request.Operation != "qam_desktop_register" && request.DesktopView != null)
                || (request.Operation == "qam_desktop_register" && (request.DesktopView == null || !request.DesktopView.Valid))
                || (configure ? request.Values == null || request.Values.Length != 3 || request.Values[0] < 0 || request.Values[0] > 1
                    || !IsMode((LegionShortcutMode)request.Values[1]) || !IsMode((LegionShortcutMode)request.Values[2]) : request.Values != null))
                throw new ArgumentException("El menú rápido solo admite abrir, consultar o guardar su inicio y botón Legion.");
        }
    }
    [DataContract]
    public sealed class DesktopViewRegistration
    {
        [DataMember] public long Window { get; set; }
        [DataMember] public string ViewId { get; set; }
        public bool Valid => Window > 0 && Guid.TryParseExact(ViewId, "N", out Guid _);
    }
    // Distinct L/R/L+R gestures are resolved on complete release; the chord never fires both singles.
    public sealed class MenuShortcutRouter
    {
        private bool armed, confirmed;
        private int seen;
        private long started = -1, last = -1;
        public void Reset() { armed = confirmed = false; seen = 0; started = last = -1; }
        public LegionShortcutMode Observe(int buttons, long now)
        {
            if (buttons < 0 || buttons > 3) { Reset(); return LegionShortcutMode.Disabled; }
            if (last >= 0 && (now < last || now - last > 1000)) Reset();
            last = now;
            if (buttons == 0) {
                var result = armed && confirmed ? (LegionShortcutMode)seen : LegionShortcutMode.Disabled;
                armed = true; confirmed = false; seen = 0; started = -1;
                return result;
            }
            if (!armed) return LegionShortcutMode.Disabled;
            if (started < 0) started = now;
            seen |= buttons;
            if (now - started >= 60) confirmed = true;
            return LegionShortcutMode.Disabled;
        }
    }
    [DataContract]
    public sealed class QamInstanceRequest
    {
        public const string PipePrefix = "LegionTweaks.Z1E.QamInstance.v1.";
        [DataMember] public int Version { get; set; } = Protocol.Version;
        [DataMember] public string Operation { get; set; }
        [DataMember] public string RequestId { get; set; }
        [DataMember] public long DesktopWindow { get; set; }
        [DataMember] public int DesktopProcessId { get; set; }
        [DataMember] public string DesktopViewId { get; set; }
        public bool Valid => Version == Protocol.Version && (Operation == "status" ? RequestId == null
            : (Operation == "open" || Operation == "toggle" || Operation == "desktop_toggle") && Guid.TryParseExact(RequestId, "N", out Guid _))
            && (Operation == "desktop_toggle" ? DesktopWindow >= 0 && DesktopProcessId >= 0
                && (DesktopWindow == 0 ? DesktopProcessId == 0 && DesktopViewId == null : DesktopProcessId > 0 && Guid.TryParseExact(DesktopViewId, "N", out Guid _))
                : DesktopWindow == 0 && DesktopProcessId == 0 && DesktopViewId == null);
    }
    [DataContract]
    public sealed class QamInstanceResponse
    {
        [DataMember] public int Version { get; set; } = Protocol.Version;
        [DataMember] public bool Success { get; set; }
        [DataMember] public string RequestId { get; set; }
        [DataMember] public bool Running { get; set; }
        [DataMember] public bool Visible { get; set; }
        [DataMember] public int ProcessId { get; set; }
        [DataMember] public int SessionId { get; set; }
        [DataMember] public string Message { get; set; }
    }
}
