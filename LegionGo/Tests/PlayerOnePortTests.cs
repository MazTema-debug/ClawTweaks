// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LegionTweaks;

internal static partial class Program
{
    private static void PlayerOnePortTests()
    {
        Test("Player one identifies only the original Go XUSB interface and rejects parents siblings and lookalikes", () => {
            foreach (string id in new[] { @"USB\VID_17EF&PID_6182&MI_00\sample", @"usb\vid_17ef&pid_61eb&mi_00\sample" })
                Assert(PlayerOnePort.IsPhysicalEndpoint(id), "original XUSB interface rejected");
            foreach (string id in new[] { null, "", @"USB\VID_17EF&PID_61EB\parent", @"USB\VID_17EF&PID_61EB&MI_01\buttons",
                @"USB\VID_17EF&PID_61820&MI_00\lookalike", @"USB\VID_045E&PID_028E\virtual", @"USB\VID_17EF&PID_61EB&MI_00\a\b" })
                Assert(!PlayerOnePort.IsPhysicalEndpoint(id), "unrelated interface treated as physical source");
        });
        Test("Player one XInput inspection matches the Windows ABI without calling native APIs", () => {
            var type = typeof(PlayerOnePort);
            Assert(Marshal.SizeOf(type.GetNestedType("NativeState", BindingFlags.NonPublic)) == 16, "XINPUT_STATE layout");
            Assert(Marshal.SizeOf(type.GetNestedType("Pad", BindingFlags.NonPublic)) == 12, "XINPUT_GAMEPAD layout");
        });
    }
}
