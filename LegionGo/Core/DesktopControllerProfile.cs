// SPDX-License-Identifier: AGPL-3.0-only
namespace LegionTweaks
{
    public static class DesktopControllerProfile
    {
        public const string Name = "Desktop";

        // Built into the helper. Never read from the editable profile collection,
        // IPC layout data or a saved power profile. Each caller owns a fresh copy.
        public static ControllerLayout CreateLayout() => new ControllerLayout {
            Enabled = true,
            Map = new[] { 4096, 8192, 16384, 32768, 256, 512, 32, 16, 64, 128, 1, 2, 4, 8, 1024 },
            LeftDeadzone = 10, RightDeadzone = 10, Rumble = 100,
            LegionMap = new int[7], SwapSticks = false, SwapTriggers = false,
            InvertLeftY = false, InvertRightY = false
        };
    }
}
