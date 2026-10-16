// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static ProfileDefinition TransferProfile(string name) => new ProfileDefinition { Id = Guid.NewGuid().ToString("D"), Name = name,
        Mode = 255, Limits = new[] { 10, 15, 20 }, BoostAC = 2, BoostDC = 0, EppAC = 35, EppDC = 80, WindowsMode = 1,
        Controller = new ControllerLayout { Enabled = true, InvertRightY = true } };
    private static ProfileCatalog TransferCatalog(params ProfileDefinition[] profiles) => new ProfileCatalog { Profiles = profiles.ToList() };
    private static void ProfileTransferTests()
    {
        Test("Profile export roundtrip preserves Unicode AC/DC values and stored layout without applying", () => {
            var source = TransferCatalog(TransferProfile("Diabló · batería")); string before = Protocol.Encode(source);
            var imported = ProfileTransfer.Parse(ProfileTransfer.Export(source));
            Assert(Protocol.Encode(imported) == before && Protocol.Encode(source) == before, "export or parse mutated settings");
            imported.Profiles[0].Limits[0] = 5;
            Assert(source.Profiles[0].Limits[0] == 10, "parsed catalogue aliases source");
        });
        Test("Profile import appends new IDs and never overwrites existing profiles", () => {
            var p = TransferProfile("Batería"); var current = TransferCatalog(p); string before = Protocol.Encode(current);
            var result = ProfileTransfer.MergeCopies(current, TransferCatalog(p.Copy()));
            Assert(result.Profiles.Count == 2 && result.Profiles[0].Id == p.Id && result.Profiles[1].Id != p.Id, "existing ID overwritten");
            Assert(result.Profiles[1].Name == "Batería (importado 1)" && Protocol.Encode(current) == before, "name collision or source mutation");
        });
        Test("Import resolves trimmed case-insensitive name collisions including previous imports", () => {
            var current = TransferCatalog(TransferProfile("BATERÍA"), TransferProfile("Batería (importado 1)"));
            var result = ProfileTransfer.MergeCopies(current, TransferCatalog(TransferProfile(" batería ")));
            Assert(result.Profiles.Last().Name == "batería (importado 2)", "collision chain not handled");
        });
        Test("Long duplicate names remain bounded and never split a surrogate pair", () => {
            string name = new string('x', 26) + "😀" + new string('y', 12);
            var result = ProfileTransfer.MergeCopies(TransferCatalog(TransferProfile(name)), TransferCatalog(TransferProfile(name)));
            string imported = result.Profiles.Last().Name;
            Assert(imported.Length <= 40 && !imported.Any(char.IsSurrogate), "duplicate truncation left broken Unicode");
        });
        Test("An oversized import fails atomically and keeps all existing data", () => {
            var current = TransferCatalog(Enumerable.Range(0, 20).Select(i => TransferProfile("Perfil " + i)).ToArray());
            string before = Protocol.Encode(current);
            MustFail(() => ProfileTransfer.MergeCopies(current, TransferCatalog(TransferProfile("Extra"))));
            Assert(Protocol.Encode(current) == before, "capacity error imported partially");
        });
        Test("Unknown profile formats versions malformed JSON and excessive bytes are rejected", () => {
            string json = ProfileTransfer.Export(TransferCatalog(TransferProfile("Prueba")));
            MustFail(() => ProfileTransfer.Parse(json.Replace("LegionTweaks.Profiles", "Other.Profiles")));
            var archive = new ProfileArchive { Version = 99, Catalog = TransferCatalog(TransferProfile("Prueba")) };
            MustFail(() => ProfileTransfer.Parse(Protocol.Encode(archive)));
            MustFail(() => ProfileTransfer.Parse("{broken")); MustFail(() => ProfileTransfer.Parse(" "));
            MustFail(() => ProfileTransfer.Parse(json + new string(' ', ProfileTransfer.MaxBytes)));
            MustFail(() => ProfileTransfer.Parse("{}"));
        });
        Test("Import validates every profile and rejects duplicates or invalid power values before merge", () => {
            var p = TransferProfile("Malo"); p.Limits = new[] { 30, 5, 10 };
            MustFail(() => ProfileTransfer.Parse(Protocol.Encode(new ProfileArchive { Catalog = TransferCatalog(p) })));
            p = TransferProfile("Duplicado");
            MustFail(() => ProfileTransfer.Parse(Protocol.Encode(new ProfileArchive { Catalog = TransferCatalog(p, p.Copy()) })));
        });
        Test("Twenty complete profiles fit the versioned UTF8 transfer format", () => {
            var catalog = TransferCatalog(Enumerable.Range(0, 20).Select(i => TransferProfile("Perfil ñ " + i)).ToArray());
            Assert(ProfileTransfer.Parse(ProfileTransfer.Export(catalog)).Profiles.Count == 20, "maximum catalogue cannot export");
        });
        Test("Name keyboard replaces selections and backspaces an entire Unicode character", () => {
            var edit = ProfileNameEdit.Insert("Diablo", 0, 6, "Batería");
            Assert(edit.Text == "Batería" && edit.Caret == 7, "selection replacement");
            edit = ProfileNameEdit.Backspace("Go😀", 4, 0);
            Assert(edit.Text == "Go" && edit.Caret == 2, "split surrogate on backspace");
            edit = ProfileNameEdit.Backspace("Batería", 0, 3);
            Assert(edit.Text == "ería" && edit.Caret == 0, "selected backspace");
        });
        Test("Name keyboard rejects control characters excess length and invalid selections", () => {
            string full = new string('ñ', 40);
            Assert(ProfileNameEdit.Insert(full, 40, 0, "a").Text == full, "name length overflow");
            Assert(ProfileNameEdit.Insert("Go", 2, 0, "\n").Text == "Go", "control character accepted");
            MustFail(() => ProfileNameEdit.Insert("Go", 3, 0, "a"));
            Assert(ProfileNameEdit.Backspace("Go", 0, 0).Text == "Go", "backspace at start");
        });
    }
}
