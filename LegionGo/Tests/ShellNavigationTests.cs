// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using LegionTweaks.Widget;

internal static partial class Program
{
    private static void ShellNavigationTests()
    {
        Test("Shell opens Inicio and visits every area in both directions", () => {
            var shell = new ShellNavigation();
            Assert(shell.Current == ShellSection.Home, "initial view changed");
            foreach (var section in ShellNavigation.Sections.Skip(1))
                Assert(shell.Move(1) && shell.Current == section, "forward route skipped an area");
            Assert(shell.Move(1) && shell.Current == ShellSection.Home, "forward wrap failed");
            Assert(shell.Move(-1) && shell.Current == ShellSection.Settings, "backward wrap failed");
            foreach (var section in ShellNavigation.Sections.Reverse().Skip(1))
                Assert(shell.Move(-1) && shell.Current == section, "backward route skipped an area");
        });
        Test("Shell accepts only named known destinations", () => {
            var shell = new ShellNavigation();
            foreach (var tag in new[] { null, "", "display", "Unknown", "-1", "2", "99", "Power, Display" })
                Assert(!shell.Select(tag) && shell.Current == ShellSection.Home, "invalid route changed the area: " + tag);
            Assert(shell.Select("Display") && shell.Current == ShellSection.Display, "valid named route rejected");
            Assert(!shell.Select("Display") && shell.Current == ShellSection.Display, "same route was reported as a change");
        });
        Test("Shell rejects invalid directions and enum values without losing selection", () => {
            var shell = new ShellNavigation(); shell.Select(ShellSection.Controller);
            foreach (int direction in new[] { int.MinValue, -2, 0, 2, int.MaxValue })
                Assert(!shell.Move(direction) && shell.Current == ShellSection.Controller, "invalid movement changed area");
            Assert(!shell.Select((ShellSection)(-1)) && !shell.Select((ShellSection)99)
                && shell.Current == ShellSection.Controller, "invalid enum changed area");
        });
        Test("Shell view state and destination lists are independent between windows", () => {
            var desktop = new ShellNavigation(); var widget = new ShellNavigation();
            desktop.Select(ShellSection.Profiles); widget.Select(ShellSection.Display);
            desktop.Move(1);
            var destinations = ShellNavigation.Sections; destinations[0] = ShellSection.Games;
            Assert(desktop.Current == ShellSection.Games && widget.Current == ShellSection.Display
                && ShellNavigation.Sections[0] == ShellSection.Home, "window state or catalog was shared");
        });
        Test("Shell trigger shortcuts leave editable fields and dialogs alone", () => {
            foreach (ShellFocusContext context in Enum.GetValues(typeof(ShellFocusContext)))
                Assert(ShellNavigation.AllowsShortcut(context, false, false)
                    == (context == ShellFocusContext.Navigation || context == ShellFocusContext.Content), "editing context intercepted: " + context);
            Assert(!ShellNavigation.AllowsShortcut((ShellFocusContext)99, false, false), "unknown context intercepted");
        });
        Test("Shell trigger shortcuts are blocked throughout work and modal flows", () => {
            foreach (ShellFocusContext context in Enum.GetValues(typeof(ShellFocusContext))) {
                Assert(!ShellNavigation.AllowsShortcut(context, true, false), "busy shortcut allowed");
                Assert(!ShellNavigation.AllowsShortcut(context, false, true), "modal shortcut allowed");
                Assert(!ShellNavigation.AllowsShortcut(context, true, true), "busy modal shortcut allowed");
            }
        });
        Test("Shell uses compact navigation and a single tile column when space is insufficient", () => {
            Assert(ShellNavigation.CompactNavigation(0) && ShellNavigation.CompactNavigation(276)
                && ShellNavigation.CompactNavigation(447) && ShellNavigation.CompactNavigation(double.NaN), "compact menu does not fit");
            Assert(!ShellNavigation.CompactNavigation(448) && !ShellNavigation.CompactNavigation(900), "wide menu hidden");
            Assert(ShellNavigation.TileColumns(200) == 1 && ShellNavigation.TileColumns(320) == 2
                && ShellNavigation.TileColumns(600) == 3, "tiles cannot adapt to viewport");
        });
    }
}
