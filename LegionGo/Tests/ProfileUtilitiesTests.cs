// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void ProfileUtilitiesTests()
    {
        Test("Duplicating uses the saved profile and creates an independent unsaved draft", () => {
            var saved = DraftProfile(); saved.Controller.LegionMap = new[] { 4096, 8192, 0, 0, 0, 0, 0 };
            var catalog = new ProfileCatalog(); catalog.Profiles.Add(saved);
            string before = Protocol.Encode(catalog);
            var copy = ProfileEditing.DuplicateSaved(catalog, saved.Id);
            Assert(copy.Id != saved.Id && copy.Name == saved.Name + " (copia)" && catalog.Profiles.Count == 1,
                "copy reused the saved identity or entered the catalog before Save");
            Assert(!ProfileDraft.Equivalent(saved, copy), "new copy did not represent a pending draft");
            copy.Limits[0] = 5; copy.Controller.Map[0] = 0; copy.Controller.LegionMap[0] = 0; copy.EppDC = 99;
            Assert(Protocol.Encode(catalog) == before, "editing the copy mutated saved arrays or settings");
            var next = catalog.Upsert(copy);
            Assert(next.Profiles.Count == 2 && catalog.Profiles.Count == 1 && next.Profiles.Any(x => x.Id == saved.Id),
                "explicit save replaced the original or mutated its catalog");
        });
        Test("Duplicated names are unique after trim and case comparison and fit forty characters", () => {
            var saved = DraftProfile(); saved.Name = new string('ñ', 40);
            var catalog = new ProfileCatalog(); catalog.Profiles.Add(saved);
            var first = ProfileEditing.DuplicateSaved(catalog, saved.Id); catalog = catalog.Upsert(first);
            catalog.Profiles[1].Name = first.Name.ToUpperInvariant();
            var second = ProfileEditing.DuplicateSaved(catalog, saved.Id);
            Assert(first.Name.Length <= 40 && second.Name.Length <= 40 && second.Name.EndsWith(" (copia 2)"),
                "long or case-colliding copy name was not bounded and unique");
            Assert(second.Name.StartsWith("ñ") && saved.Name.Length == 40, "Unicode source name was damaged");
        });
        Test("Duplicating at the catalog limit is an editable draft and Save retains the twenty-profile limit", () => {
            var catalog = new ProfileCatalog();
            for (int i = 0; i < 20; i++) { var profile = DraftProfile(); profile.Name = "Juego " + i; catalog.Profiles.Add(profile); }
            string before = Protocol.Encode(catalog);
            var copy = ProfileEditing.DuplicateSaved(catalog, catalog.Profiles[0].Id);
            Assert(copy != null && catalog.Profiles.Count == 20, "draft duplication prematurely saved or rejected a copy");
            MustFail(() => catalog.Upsert(copy));
            Assert(Protocol.Encode(catalog) == before, "a rejected twenty-first save changed the saved catalog");
            MustFail(() => ProfileEditing.DuplicateSaved(catalog, Guid.NewGuid().ToString("D")));
        });
        Test("Reset controller changes only the draft layout while preserving power and controller selection", () => {
            foreach (int choice in new[] { 0, 1, 2 })
            {
                var draft = DraftProfile();
                if (choice == 0) draft.Controller = null;
                else
                {
                    draft.Controller.Enabled = choice == 2; draft.Controller.Map[0] = 0;
                    draft.Controller.LeftDeadzone = 30; draft.Controller.RightDeadzone = 25;
                    draft.Controller.InvertLeftY = draft.Controller.InvertRightY = true;
                    draft.Controller.SwapSticks = draft.Controller.SwapTriggers = true; draft.Controller.Rumble = 0;
                    draft.Controller.LegionMap = new[] { 4096, 8192, 0, 0, 0, 0, 0 };
                }
                string before = Protocol.Encode(draft);
                var reset = ProfileEditing.ResetController(draft);
                Assert(Protocol.Encode(draft) == before && !ReferenceEquals(draft, reset), "reset mutated its source draft");
                Assert(reset.Id == draft.Id && reset.Name == draft.Name && reset.Limits.SequenceEqual(draft.Limits)
                    && reset.Mode == draft.Mode && reset.BoostAC == draft.BoostAC && reset.BoostDC == draft.BoostDC
                    && reset.EppAC == draft.EppAC && reset.EppDC == draft.EppDC && reset.WindowsMode == draft.WindowsMode,
                    "reset changed identity or power settings");
                if (choice == 0) Assert(reset.Controller == null, "Keep became a physical or virtual change");
                else
                {
                    var expected = new ControllerLayout { Enabled = choice == 2 };
                    Assert(Protocol.Encode(reset.Controller) == Protocol.Encode(expected), "reset missed a layout field or changed Physical/Virtual");
                    Assert(!ProfileDraft.Equivalent(draft, reset), "reset masked the pending layout edit by replacing its baseline");
                }
            }
        });
    }
}
