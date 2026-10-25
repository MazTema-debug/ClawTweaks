// SPDX-License-Identifier: AGPL-3.0-only
namespace LegionTweaks
{
    public static class DesktopTrayProtocol
    {
        public const string ServiceName = "LegionTweaks.DesktopTray";
        public const int Version = 1;
    }

    // Per desktop view, called only on its dispatcher. Never attach to a widget.
    public sealed class DesktopTrayState
    {
        public bool Ready { get; private set; }
        public bool Hiding { get; private set; }
        public bool Hidden { get; private set; }
        public bool Exiting { get; private set; }
        public bool InterceptClose => Ready && !Hiding && !Exiting;
        public void Connected() { if (!Exiting) Ready = true; }
        public bool BeginHide()
        {
            if (!Ready || Hiding || Hidden || Exiting) return false;
            Hiding = true; return true;
        }
        public void EndHide(bool success) { Hiding = false; Hidden = success; }
        public void Restored() { Hidden = false; }
        public bool Disconnected()
        {
            Ready = false;
            return (Hidden || Hiding) && !Exiting;
        }
        public void Exit() { Exiting = true; Ready = false; }
    }
}
