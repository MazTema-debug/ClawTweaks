// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LegionTweaks
{
    // Callback registration also opts a desktop app into Modern Standby power
    // notifications. Keep the delegate rooted until native unregistration.
    internal sealed class ControllerPowerNotifications : IDisposable
    {
        private const uint DeviceNotifyCallback = 2; // powrprof.h, not DEVICE_NOTIFY_ALL_INTERFACE_CLASSES.
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint Callback(IntPtr context, uint type, IntPtr setting);
        [StructLayout(LayoutKind.Sequential)]
        private struct Subscription { internal Callback Callback; internal IntPtr Context; }
        private readonly Callback callback;
        private IntPtr registration;
        internal ControllerPowerNotifications(Action<bool> notify)
        {
            callback = (context, type, setting) => {
                try
                {
                    if (type == 4) notify(true); // PBT_APMSUSPEND
                    else if (type == 7 || type == 18) notify(false); // RESUMESUSPEND / RESUMEAUTOMATIC
                }
                catch { } // Never let a managed exception cross a native callback.
                return 0;
            };
            var recipient = new Subscription { Callback = callback };
            uint result = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref recipient, out registration);
            if (result != 0) throw new Win32Exception((int)result, "No se pudieron registrar los avisos de suspensión del mando.");
        }
        public void Dispose()
        {
            if (registration == IntPtr.Zero) return;
            uint result = PowerUnregisterSuspendResumeNotification(registration);
            if (result != 0) throw new Win32Exception((int)result);
            registration = IntPtr.Zero;
            GC.KeepAlive(callback);
        }
        [DllImport("powrprof.dll")]
        private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Subscription recipient, out IntPtr registration);
        [DllImport("powrprof.dll")]
        private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
    }
}
