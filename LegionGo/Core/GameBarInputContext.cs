// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class GameBarInputContext
    {
        [DataMember] public string Owner { get; set; }
        [DataMember] public bool Foreground { get; set; }
        [DataMember] public long ObservedUtc { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool WidgetFocused { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool SdkForeground { get; set; }
        public void Validate()
        {
            Guid id;
            if (Owner == null || !Guid.TryParseExact(Owner, "N", out id) || id == Guid.Empty
                || ObservedUtc <= 0 || ObservedUtc > DateTime.MaxValue.Ticks) throw new ArgumentException("Contexto de Game Bar inválido.");
        }
        public GameBarInputContext Copy() => new GameBarInputContext { Owner = Owner, Foreground = Foreground, ObservedUtc = ObservedUtc,
            WidgetFocused = WidgetFocused, SdkForeground = SdkForeground };
        public static bool UsesDirectInput(bool hostedWidget, bool sdkForeground, bool widgetVisible, bool widgetFocused)
            => hostedWidget && (sdkForeground || (widgetVisible && widgetFocused));
    }
    public interface IGameBarControllerInput
    {
        void ObserveGameBarInput(GameBarInputContext context);
    }
    public interface IGameBarForeground
    {
        bool IsForeground();
    }
    // Independent view leases: a desktop poll cannot clear a widget's lease,
    // and a delayed/closed view cannot reactivate it with an older observation.
    public sealed class GameBarInputLease
    {
        public const int LeaseMilliseconds = 4000;
        private readonly Dictionary<string, GameBarInputContext> views = new Dictionary<string, GameBarInputContext>();
        public void Observe(GameBarInputContext context, long now)
        {
            context.Validate();
            long age = now - context.ObservedUtc;
            if (age < 0 || age > LeaseMilliseconds * TimeSpan.TicksPerMillisecond) return;
            GameBarInputContext previous;
            if (views.TryGetValue(context.Owner, out previous) && (previous.ObservedUtc > context.ObservedUtc
                || (previous.ObservedUtc == context.ObservedUtc && (!previous.Foreground || context.Foreground)))) return;
            if (!views.ContainsKey(context.Owner) && views.Count >= 16) {
                string oldest = null; long time = long.MaxValue;
                foreach (var pair in views) if (pair.Value.ObservedUtc < time) { oldest = pair.Key; time = pair.Value.ObservedUtc; }
                if (oldest != null) views.Remove(oldest);
            }
            views[context.Owner] = context.Copy();
        }
        public bool Active(long now)
        {
            foreach (var value in views.Values) {
                long age = now - value.ObservedUtc;
                if (value.Foreground && age >= 0 && age <= LeaseMilliseconds * TimeSpan.TicksPerMillisecond) return true;
            }
            return false;
        }
        public bool UsesDirectInput(long now, bool nativeHostForeground) => nativeHostForeground || Active(now);
        public void Reset() { views.Clear(); }
    }
}
