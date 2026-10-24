// SPDX-License-Identifier: AGPL-3.0-only
namespace LegionTweaks
{
    public interface ISplitControllerService { void PrepareSplitControllers(); }

    // One combined Lenovo input, two independently submitted Xbox reports.
    // No firmware mappings, saved profiles, keyboard injection or game settings.
    public static class SplitControllerProfile
    {
        private static readonly ControllerLayout filter = CreateLayout();
        public static ControllerLayout CreateLayout()
        {
            var layout = DesktopControllerProfile.CreateLayout();
            layout.Rumble = 0;
            return layout;
        }
        public static void Transform(GamepadFrame input, out GamepadFrame left, out GamepadFrame right)
        {
            var normalized = filter.Transform(input);
            // Buttons face the player: LB/LT point left, RB/RT point right.
            // Rotate both movement and aim into the player's horizontal view.
            left = new GamepadFrame { LX = Negate(normalized.LY), LY = normalized.LX, LT = input.LT };
            right = new GamepadFrame { LX = normalized.RY, LY = Negate(normalized.RX), LT = input.RT };
            Aim(normalized.Buttons, 1, 2, 8, 4, out left.RX, out left.RY);
            Aim(normalized.Buttons, 4096, 32768, 16384, 8192, out right.RX, out right.RY);
            Map(input.Buttons, 256, 256, ref left); // LB -> LB
            Map(input.Buttons, 512, 256, ref right); // RB -> LB
            Map(input.Buttons, 64, 4096, ref left); // stick click -> A (menus)
            Map(input.Buttons, 128, 4096, ref right);
            // Both physical Menu and View buttons are on the left half.
            Map(input.Buttons, 32, 32, ref left); // View -> View, player 1
            Map(input.Buttons, 16, 16, ref left); // Menu -> Start, player 1
            // The passive reader already removes the global Game Bar shortcut.
            Map(input.LegionButtons, 1, 32, ref left);
            Map(input.LegionButtons, 2, 32, ref right);
            Map(input.LegionButtons, 4, 8192, ref left); // Y1 -> B
            Map(input.LegionButtons, 8, 512, ref left); // Y2 -> RB
            Map(input.LegionButtons, 16, 8192, ref right); // Y3 -> B
            Map(input.LegionButtons, 32, 512, ref right); // M2 -> RB
            Map(input.LegionButtons, 64, 16, ref right); // M3 -> Start, player 2
        }
        private static void Map(int input, int source, int target, ref GamepadFrame output)
        { if ((input & source) != 0) output.Buttons |= (ushort)target; }
        // -32768 has no positive short counterpart. Saturate rather than wrap.
        private static short Negate(short value) => value == short.MinValue ? short.MaxValue : (short)-value;
        private static void Aim(int input, int left, int right, int up, int down, out short x, out short y)
        {
            x = Axis((input & left) != 0, (input & right) != 0);
            y = Axis((input & down) != 0, (input & up) != 0);
        }
        private static short Axis(bool negative, bool positive) => negative == positive ? (short)0 : negative ? short.MinValue : short.MaxValue;
    }
}
