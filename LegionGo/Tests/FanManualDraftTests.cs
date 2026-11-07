// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;
using LegionTweaks.Widget;

internal static partial class Program
{
    private static Snapshot FanSample(int? rpm, int maximum = 0, string error = null) => new Snapshot { Supported = true, Values = new[] {
        new Reading { Key = "FanRpm", Value = rpm, Unit = "RPM", Source = "Lenovo WMI", Error = error },
        new Reading { Key = "FanFullSpeed", Value = maximum }
    } };
    private static void FanManualDraftTests()
    {
        Test("Fan manual UI separates edited percentage confirmed percentage and real RPM", () => {
            var draft = new FanManualDraft();
            Assert(!draft.EditedPercent.HasValue && !draft.ConfirmedPercent.HasValue && !draft.CanApply, "opening bound a hardware setting");
            draft.Edit(25); string first = draft.RpmText(FanSample(3654), true);
            Assert(draft.EditedPercent == 25 && first.Contains("3654 RPM") && first.Contains("Lenovo WMI")
                && !draft.ConfirmedPercent.HasValue && !draft.CanApply, "RPM became a confirmed percent or armed apply");
            draft.RpmText(FanSample(8700, 1), true);
            Assert(draft.EditedPercent == 25 && !draft.ConfirmedPercent.HasValue, "firmware maximum changed manual draft/confirmation");
        });
        Test("Fan manual visual endpoints remain drafts and never mean firmware maximum or a safe minimum", () => {
            var draft = new FanManualDraft();
            foreach (int percent in new[] { 0, 1, 30, 99, 100 }) {
                draft.Edit(percent);
                Assert(draft.EditedPercent == percent && draft.DraftText.Contains(percent + " %") && !draft.CanApply && draft.ConfirmedPercent == null,
                    "visual range activated a command/confirmation");
            }
            draft.RpmText(FanSample(7500, 0), true);
            Assert(draft.EditedPercent == 100 && draft.ConfirmedPercent == null, "manual 100 inferred hardware maximum");
        });
        Test("Fan draft rejects nonfinite or out-of-range values without losing the edited value", () => {
            var draft = new FanManualDraft(); draft.Edit(45);
            foreach (double value in new[] { -1, 101, double.NaN, double.NegativeInfinity, double.PositiveInfinity }) {
                bool rejected = false; try { draft.Edit(value); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Assert(rejected && draft.EditedPercent == 45 && !draft.CanApply, "bad value changed/armed draft");
            }
        });
        Test("Fan RPM presentation rejects stale missing ambiguous or errored readings without deriving speed", () => {
            var draft = new FanManualDraft(); draft.Edit(70);
            Assert(!draft.RpmText(FanSample(3654), false).Contains("3654") && draft.RpmText(FanSample(3654), false).Contains("anterior"), "stale RPM looked fresh");
            foreach (Snapshot sample in new[] { null, new Snapshot { Supported = false }, FanSample(null), FanSample(-1), FanSample(15001), FanSample(3654, error:"unavailable"),
                new Snapshot { Supported = true, Values = new[] { new Reading { Key = "FanRpm", Value = 1234 } } },
                new Snapshot { Supported = true, Values = new[] { FanSample(1234).Values[0], FanSample(4321).Values[0] } } })
                Assert(!draft.RpmText(sample, true).Contains("3654 RPM") && !draft.RpmText(sample, true).Contains("1234 RPM"), "invalid/duplicate source displayed RPM");
            Assert(draft.EditedPercent == 70 && draft.ConfirmedPercent == null, "bad sample changed percent");
        });
    }
}
