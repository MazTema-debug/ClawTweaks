// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void GameBarForegroundTests()
    {
        Test("Compact input redirection works with no widget lease or native host and clears on exit", () => {
            long now = 0; bool redirected = false;
            var probe = new NativeGameBarForeground(() => now,
                () => new MouseDesktop { Available = true, Foreground = 123 }, () => redirected, window => false);
            var leases = new GameBarInputLease();
            Assert(!leases.UsesDirectInput(DateTime.UtcNow.Ticks, probe.IsForeground()), "closed Game Bar claimed input");
            now = 100; redirected = true;
            Assert(leases.UsesDirectInput(DateTime.UtcNow.Ticks, probe.IsForeground()), "compact host depended on a widget");
            now = 200; redirected = false;
            Assert(!leases.UsesDirectInput(DateTime.UtcNow.Ticks, probe.IsForeground()), "closed compact host retained input");
        });
        Test("An unavailable input desktop prevents redirection or native-host evidence", () => {
            int reads = 0, hosts = 0;
            var probe = new NativeGameBarForeground(() => 100, () => new MouseDesktop(),
                () => { reads++; return true; }, window => { hosts++; return true; });
            Assert(!probe.IsForeground() && reads == 0 && hosts == 0, "secure desktop accepted Game Bar input");
        });
        Test("The next sample after a desktop lock drops a previous positive redirection", () => {
            long now = 0; bool interactive = true;
            var probe = new NativeGameBarForeground(() => now,
                () => new MouseDesktop { Available = interactive, Foreground = 123 }, () => true, window => false);
            Assert(probe.IsForeground(), "initial redirected input was ignored");
            now = 100; interactive = false;
            Assert(!probe.IsForeground(), "lock retained a positive sample");
        });
        Test("A failed API sample cannot replay true and still allows verified native-host fallback", () => {
            long now = 0; bool fail = false, native = false;
            var probe = new NativeGameBarForeground(() => now,
                () => new MouseDesktop { Available = true, Foreground = 123 },
                () => { if (fail) throw new InvalidOperationException(); return true; }, window => native);
            Assert(probe.IsForeground(), "initial redirected input was ignored");
            now = 100; fail = true;
            Assert(!probe.IsForeground(), "failed API sample retained true");
            now = 200; native = true;
            Assert(probe.IsForeground(), "API failure suppressed verified-host fallback");
            now = 300; native = false;
            Assert(!probe.IsForeground(), "failed API and ended native focus retained input");
        });
        Test("Game Bar observations are bounded to 100ms and a clock reversal forces a fresh sample", () => {
            long now = 1000; bool redirected = true; int reads = 0;
            var probe = new NativeGameBarForeground(() => now,
                () => new MouseDesktop { Available = true, Foreground = 123 },
                () => { reads++; return redirected; }, window => false);
            Assert(probe.IsForeground() && reads == 1, "initial observation missing");
            now = 1099; redirected = false;
            Assert(probe.IsForeground() && reads == 1, "worker loop repeatedly queried the OS");
            now = 1100;
            Assert(!probe.IsForeground() && reads == 2, "positive sample survived its 100ms limit");
            now = 900;
            Assert(!probe.IsForeground() && reads == 3, "clock reversal reused a cached observation");
        });
        Test("A desktop inspection failure clears cached Game Bar evidence", () => {
            long now = 0; bool fail = false;
            var probe = new NativeGameBarForeground(() => now, () => {
                if (fail) throw new InvalidOperationException();
                return new MouseDesktop { Available = true, Foreground = 123 };
            }, () => true, window => true);
            Assert(probe.IsForeground(), "initial observation missing");
            now = 100; fail = true;
            Assert(!probe.IsForeground(), "failed desktop inspection replayed a positive observation");
        });
        Test("Widget-free compact detection preserves release gates on both entry and return to the game", () => {
            long now = 0; bool redirected = false;
            var probe = new NativeGameBarForeground(() => now,
                () => new MouseDesktop { Available = true, Foreground = 123 }, () => redirected, window => false);
            var route = new ControllerInputRouteGate(); var layout = new ControllerLayout();
            var held = new GamepadFrame { Buttons = 4096 }; var neutral = new GamepadFrame();
            Assert(route.Accept(probe.IsForeground(), true, held, layout), "ordinary game input changed");
            now = 100; redirected = true;
            Assert(!route.Accept(probe.IsForeground(), true, held, layout), "activation button leaked into Game Bar");
            Assert(route.Accept(probe.IsForeground(), true, neutral, layout), "release failed to arm compact input");
            Assert(route.Accept(probe.IsForeground(), true, held, layout), "new press was lost");
            now = 200; redirected = false;
            Assert(!route.Accept(probe.IsForeground(), true, neutral, layout, held), "filtered SDL zero released a still-held button");
            Assert(route.Accept(probe.IsForeground(), true, neutral, layout, neutral), "return to game failed to rearm after release");
        });
    }
}
