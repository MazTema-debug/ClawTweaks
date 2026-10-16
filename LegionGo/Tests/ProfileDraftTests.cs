// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static ProfileDefinition DraftProfile() => new ProfileDefinition {
        Id = Guid.NewGuid().ToString("D"), Name = "Juego con ñ y acento á", Mode = 255,
        Limits = new[] { 10, 15, 20 }, BoostAC = 2, BoostDC = 0, EppAC = 20, EppDC = 70,
        WindowsMode = 1, Controller = new ControllerLayout { Enabled = true }
    };

    private static void ProfileDraftTests()
    {
        Test("Profile draft equality ignores identity and normalized name whitespace", () => {
            var saved = DraftProfile(); var draft = saved.Copy();
            draft.Id = Guid.NewGuid().ToString("D"); draft.Name = "  " + saved.Name + "  ";
            Assert(ProfileDraft.Equivalent(saved, draft), "identity or discarded whitespace marked the draft dirty");
            draft.Name = saved.Name.ToUpperInvariant();
            Assert(!ProfileDraft.Equivalent(saved, draft), "actual Unicode/case edit was ignored");
        });
        Test("Profile draft tracks independent power changes and reversions", () => {
            var saved = DraftProfile();
            Action<ProfileDefinition>[] changes = {
                x => x.Mode = 2, x => x.Limits[0]++, x => x.Limits[1]++, x => x.Limits[2]++,
                x => x.BoostAC = null, x => x.BoostDC = 2, x => x.EppAC = null,
                x => x.EppDC = 20, x => x.WindowsMode = 2, x => x.Name += " 2"
            };
            foreach (var change in changes) {
                var draft = saved.Copy(); change(draft);
                Assert(!ProfileDraft.Equivalent(saved, draft), "power/name edit was ignored");
                Assert(ProfileDraft.Equivalent(saved, saved.Copy()), "reverted draft remained dirty");
            }
        });
        Test("Profile draft ignores unused limits without changing the profile", () => {
            var saved = DraftProfile(); saved.Mode = 2;
            var draft = saved.Copy(); draft.Limits = new[] { 25, 25, 30 };
            Assert(ProfileDraft.Equivalent(saved, draft), "inactive custom sliders marked dirty");
            Assert(saved.Limits[0] == 10 && draft.Limits[0] == 25, "comparison normalized its arguments by mutation");
            draft.Mode = 255;
            Assert(!ProfileDraft.Equivalent(saved, draft), "enabling custom limits was ignored");
        });
        Test("Profile draft distinguishes conserving, physical and virtual controller modes", () => {
            var saved = DraftProfile(); var draft = saved.Copy();
            draft.Controller = null;
            Assert(!ProfileDraft.Equivalent(saved, draft), "conserve and virtual treated alike");
            saved.Controller.Enabled = false;
            Assert(!ProfileDraft.Equivalent(saved, draft), "conserve and physical treated alike");
            draft.Controller = saved.Controller.Copy();
            draft.Controller.LeftDeadzone = 29; draft.Controller.Map[0] = 0; draft.Controller.Rumble = 0;
            Assert(ProfileDraft.Equivalent(saved, draft), "inactive virtual settings marked physical profile dirty");
            draft.Controller.Enabled = true;
            Assert(!ProfileDraft.Equivalent(saved, draft), "enabling virtual mode was ignored");
        });
        Test("Profile draft tracks every active controller setting", () => {
            var saved = DraftProfile();
            Action<ControllerLayout>[] changes = {
                x => x.Map[0] = 0, x => x.LeftDeadzone++, x => x.RightDeadzone++,
                x => x.SwapSticks = true, x => x.InvertLeftY = true, x => x.InvertRightY = true,
                x => x.SwapTriggers = true, x => x.Rumble = 0
            };
            foreach (var change in changes) {
                var draft = saved.Copy(); change(draft.Controller);
                Assert(!ProfileDraft.Equivalent(saved, draft), "active layout edit was ignored");
            }
        });
        Test("Profile draft handles incomplete comparisons without inventing a saved profile", () => {
            var saved = DraftProfile();
            Assert(ProfileDraft.Equivalent(null, null) && !ProfileDraft.Equivalent(saved, null), "missing profile comparison");
            Assert(!ProfileDraft.Equivalent(null, saved), "missing baseline treated as equal");
            var draft = saved.Copy(); draft.Limits = null;
            Assert(!ProfileDraft.Equivalent(saved, draft), "missing custom limits treated as saved limits");
            draft = saved.Copy(); draft.Controller.Map = null;
            Assert(!ProfileDraft.Equivalent(saved, draft), "missing button map treated as saved map");
        });
        Test("Saved profile summary identifies the applied power and controller version", () => {
            var saved = DraftProfile(); string text = ProfileDraft.DescribeSaved(saved);
            Assert(text.Contains(saved.Name) && text.Contains("Versión guardada") && text.Contains("10 / 15 / 20 W"), "missing saved identity or limits");
            Assert(text.Contains("Boost AC: Activado · DC: Desactivado") && text.Contains("EPP AC: 20 · DC: 70"), "AC/DC summary merged");
            Assert(text.Contains("distribución guardada"), "saved virtual layout not identified");
            saved.Mode = null; saved.Limits = null; saved.Controller = null;
            Assert(ProfileDraft.DescribeSaved(saved).Contains("Conservar el mando actual"), "conserve mode summary");
            saved.Controller = new ControllerLayout { Enabled = false };
            Assert(ProfileDraft.DescribeSaved(saved).Contains("Usar mando físico"), "physical mode summary");
        });
    }
}
