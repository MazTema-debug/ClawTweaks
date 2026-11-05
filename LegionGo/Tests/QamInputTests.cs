// SPDX-License-Identifier: AGPL-3.0-only
using LegionTweaks.QamProbe;

internal static partial class Program
{
    private static QamAction QamReport(QamInput input, long now, byte front = 0, byte face = 0, bool focused = false)
    {
        var report = LegionReport(false, front);
        report[19] = face;
        return input.Observe(report, now, focused);
    }

    private static void QamInputTests()
    {
        Test("QAM left emits one toggle on release and ignores startup holds and noise", () => {
            var input = new QamInput();
            Assert(QamReport(input, 0, 0x80) == QamAction.None && QamReport(input, 90, 0x80) == QamAction.None, "startup hold fired");
            Assert(QamReport(input, 100) == QamAction.None, "startup release fired");
            QamReport(input, 120, 0x80);
            Assert(QamReport(input, 130) == QamAction.None, "noise fired");
            QamReport(input, 150, 0x80);
            Assert(QamReport(input, 220, 0x80) == QamAction.None && QamReport(input, 300, 0x80) == QamAction.None, "hold fired before release");
            Assert(QamReport(input, 310) == QamAction.Toggle && QamReport(input, 320) == QamAction.None, "release did not toggle exactly once");
        });
        Test("QAM gives the right button priority and cancels a pending left toggle", () => {
            var input = new QamInput();
            QamReport(input, 0, focused: true);
            QamReport(input, 20, 0x80, focused: true);
            QamReport(input, 90, 0x80, focused: true);
            Assert(QamReport(input, 100, 0xC0, focused: true) == QamAction.YieldToGameBar, "right did not yield");
            Assert(QamReport(input, 110, 0x40, focused: true) == QamAction.None, "right repeated");
            Assert(QamReport(input, 120, focused: true) == QamAction.None, "cancelled left toggle fired");
            Assert(QamReport(input, 130, 0x40) == QamAction.None && QamReport(input, 200) == QamAction.None, "right opened hidden QAM");
        });
        Test("QAM drops a pending toggle across stale HID reports", () => {
            var input = new QamInput();
            QamReport(input, 0);
            QamReport(input, 30, 0x80);
            QamReport(input, 100, 0x80);
            Assert(input.Observe(null, 400, false) == QamAction.None, "timeout fired");
            Assert(QamReport(input, 420) == QamAction.None, "stale release opened QAM");
        });
        Test("QAM navigation requires focus and neutral then moves once per D-pad press", () => {
            var input = new QamInput();
            QamReport(input, 0, 0x04);
            Assert(QamReport(input, 15, 0x04, focused: true) == QamAction.None, "held D-pad acted on focus");
            QamReport(input, 25, focused: true);
            Assert(QamReport(input, 35, 0x04, focused: true) == QamAction.Down, "D-pad did not move");
            Assert(QamReport(input, 45, 0x04, focused: true) == QamAction.None, "held D-pad repeated");
            QamReport(input, 55, focused: true);
            Assert(QamReport(input, 65, 0x08) == QamAction.None, "D-pad acted outside panel");
        });
        Test("QAM A and B act on release and cannot leak a pending action across focus loss", () => {
            var input = new QamInput();
            QamReport(input, 0, focused: true);
            Assert(QamReport(input, 10, face: 0x80, focused: true) == QamAction.None, "A fired while held");
            Assert(QamReport(input, 20, focused: true) == QamAction.Accept, "A release not accepted");
            Assert(QamReport(input, 30, face: 0x40, focused: true) == QamAction.None, "B fired while held");
            QamReport(input, 40, face: 0x40);
            Assert(QamReport(input, 50, focused: true) == QamAction.None, "old B closed newly focused panel");
            QamReport(input, 60, face: 0x40, focused: true);
            Assert(QamReport(input, 70, focused: true) == QamAction.Close, "B release did not close");
        });
        Test("QAM shoulder tabs act once on release in an eligible panel", () => {
            var input = new QamInput();
            QamReport(input, 0, face: 0x08, focused: true);
            Assert(QamReport(input, 10, focused: true) == QamAction.None, "startup shoulder press acted");
            Assert(QamReport(input, 20, face: 0x08, focused: true) == QamAction.None, "LB acted before release");
            Assert(QamReport(input, 30, focused: true) == QamAction.PreviousTab, "LB release did not select previous tab");
            Assert(QamReport(input, 40, focused: true) == QamAction.None, "LB repeated");
            QamReport(input, 50, face: 0x02, focused: true);
            Assert(QamReport(input, 60, focused: true) == QamAction.NextTab, "RB release did not select next tab");
            QamReport(input, 70, face: 0x02);
            Assert(QamReport(input, 80) == QamAction.None, "shoulder changed a hidden panel");
        });
        Test("QAM tab switching cancels held A and rejects the two-shoulder chord", () => {
            var input = new QamInput();
            QamReport(input, 0, focused: true);
            QamReport(input, 10, face: 0x80, focused: true);
            QamReport(input, 20, face: 0x82, focused: true);
            Assert(QamReport(input, 30, focused: true) == QamAction.NextTab, "held A activated content during a tab change");
            QamReport(input, 40, face: 0x08, focused: true);
            QamReport(input, 50, face: 0x0A, focused: true);
            Assert(QamReport(input, 60, focused: true) == QamAction.None, "both shoulders changed a tab");
        });
    }
}
