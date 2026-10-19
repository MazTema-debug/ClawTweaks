// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.ExceptionServices;

namespace LegionTweaks
{
    public interface IControllerModeCyclePort
    {
        void CheckInitial();
        void EnterDirectInput();
        void VerifyTemporary();
        void RestoreOriginal();
        void VerifyOriginal();
        void CompleteRecovery();
    }

    // Explicit diagnostic only: no virtual target or power-profile operation.
    public static class ControllerModeCycle
    {
        public static void Run(IControllerModeCyclePort port)
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            port.CheckInitial();
            bool pending = false;
            Exception failure = null;
            try {
                // A setter can change state and then throw before returning.
                pending = true;
                port.EnterDirectInput();
                port.VerifyTemporary();
                port.RestoreOriginal();
                port.VerifyOriginal();
                port.CompleteRecovery();
                pending = false;
            }
            catch (Exception error) { failure = error; }
            // Recover before propagating the original failure. Throwing a different
            // exception from finally depends on runtime exception unwinding when
            // the caller handles only the combined error.
            if (pending) {
                try { port.RestoreOriginal(); port.VerifyOriginal(); port.CompleteRecovery(); }
                catch (Exception recovery) {
                    if (failure != null) throw new AggregateException("No se confirmó toda la recuperación del modo físico.", failure, recovery);
                    throw;
                }
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
