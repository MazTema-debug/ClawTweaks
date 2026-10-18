// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void ProfileComparisonTests()
    {
        Test("Saved profile comparison ignores identity and name without promising hardware equivalence", () => {
            var a = DraftProfile(); var b = a.Copy(); b.Id = Guid.NewGuid().ToString("D"); b.Name = "Otro juego";
            var result = ProfileComparison.Compare(a, b);
            Assert(result.Differences.Count == 0 && result.LeftName == a.Name && result.RightName == b.Name,
                "identity was treated as a power or controller change");
            string description = ProfileComparison.Describe(result);
            Assert(description.Contains("No hay diferencias") && description.Contains("no confirma el estado del hardware")
                && description.Contains("sin leer ni cambiar"), "comparison claimed a live state or game compatibility");
        });
        Test("Comparison keeps AC and DC separate and explains unchanged versus explicit zero", () => {
            var a = DraftProfile(); var b = a.Copy();
            a.BoostAC = null; a.BoostDC = 0; a.EppAC = null; a.EppDC = 70;
            b.BoostAC = 0; b.BoostDC = 2; b.EppAC = 0; b.EppDC = null; b.WindowsMode = 0;
            var result = ProfileComparison.Compare(a, b);
            Assert(result.Differences.Count == 5, "AC/DC fields were merged or missed");
            Assert(result.Differences.Single(x => x.Setting == "Boost con cargador (AC)").Left == "Sin cambiar"
                && result.Differences.Single(x => x.Setting == "Boost con cargador (AC)").Right == "Desactivado", "null boost became zero");
            Assert(result.Differences.Single(x => x.Setting == "EPP con cargador (AC)").Right == "0"
                && result.Differences.Single(x => x.Setting == "EPP con batería (DC)").Right == "Sin cambiar", "null EPP became an applied default");
        });
        Test("Comparison distinguishes custom limits from Lenovo mode and never invents limits", () => {
            var a = DraftProfile(); var b = a.Copy(); b.Mode = 2; b.Limits = null;
            var result = ProfileComparison.Compare(a, b);
            Assert(result.Differences.Count == 2 && result.Differences.Single(x => x.Setting == "SPL / SPPT / FPPT").Left == "10 / 15 / 20 W"
                && result.Differences.Single(x => x.Setting == "SPL / SPPT / FPPT").Right == "No se aplican límites personalizados", "preset mode acquired custom watts");
            a.Mode = null; a.Limits = null;
            Assert(ProfileComparison.Compare(a, b).Differences.Count == 1, "absent preset limits became a false change");
            b = a.Copy(); b.Mode = 255; b.Limits = new[] { 5, 5, 30 };
            Assert(ProfileComparison.Describe(a, b).Contains("5 / 5 / 30 W"), "boundary custom limits were omitted");
        });
        Test("Comparison treats absent optional Legion map as all unassigned and identifies L and R", () => {
            var a = DraftProfile(); var b = a.Copy(); b.Controller.LegionMap = new int[7];
            Assert(ProfileComparison.Compare(a, b).Differences.Count == 0, "old optional map created false differences");
            b.Controller.LegionMap[0] = 4096; b.Controller.LegionMap[1] = 8192;
            var result = ProfileComparison.Compare(a, b);
            Assert(result.Differences.Count == 2 && result.Differences[0].Setting == "Legion L"
                && result.Differences[0].Left == "Sin asignación virtual" && result.Differences[0].Right == "A"
                && result.Differences[1].Setting == "Legion R" && result.Differences[1].Right == "B", "Legion slots or targets were confused");
            Assert(ProfileComparison.Describe(result).Contains("reservadas por el atajo global"), "global reservation limit was hidden");
        });
        Test("Comparison labels dormant physical layouts without conflating Conserve Physical or Virtual", () => {
            var a = DraftProfile(); var b = a.Copy(); a.Controller.Enabled = b.Controller.Enabled = false;
            b.Controller.Map[0] = 0; b.Controller.LeftDeadzone = 0;
            var physical = ProfileComparison.Compare(a, b);
            Assert(physical.Differences.Count == 2 && physical.Differences.All(x => x.Left.Contains("no se aplica en modo físico")
                && x.Right.Contains("no se aplica en modo físico")), "stored physical settings were presented as active remapping");
            a.Controller = null;
            var preserve = ProfileComparison.Compare(a, b);
            Assert(preserve.Differences.Single(x => x.Setting == "Mando").Left == "Conservar el mando actual"
                && preserve.Differences.Single(x => x.Setting == "Mando").Right.Contains("detener el virtual"), "conserve was interpreted as stopping the virtual");
            a = b.Copy(); a.Controller.Enabled = true;
            var active = ProfileComparison.Compare(a, b);
            Assert(active.Differences.Count == 1 && active.Differences[0].Setting == "Mando"
                && active.Differences[0].Left == "Activar Xbox 360 virtual", "controller mode change was missed or stored layout changes invented");
        });
        Test("Comparison reports every standard controller setting and stays independent of later profile edits", () => {
            var a = DraftProfile(); var b = a.Copy(); b.Controller.Map[0] = 8192; b.Controller.LeftDeadzone = 30;
            b.Controller.RightDeadzone = 0; b.Controller.SwapSticks = true; b.Controller.InvertLeftY = true;
            b.Controller.InvertRightY = true; b.Controller.SwapTriggers = true; b.Controller.Rumble = 0;
            string beforeA = Protocol.Encode(a), beforeB = Protocol.Encode(b);
            var result = ProfileComparison.Compare(a, b); string text = ProfileComparison.Describe(result);
            Assert(result.Differences.Count == 8 && result.Differences.Single(x => x.Setting == "Botón A").Right == "B"
                && result.Differences.Single(x => x.Setting == "Vibración").Right == "0 %", "an actual standard setting was omitted");
            Assert(Protocol.Encode(a) == beforeA && Protocol.Encode(b) == beforeB, "comparison modified its input profiles");
            a.Name = "Editado"; b.Controller.Map[0] = 0; b.Limits[0] = 5;
            Assert(ProfileComparison.Describe(result) == text, "result retained mutable profile data");
        });
        Test("Comparison validates both profiles including dormant layouts and invalid special mappings", () => {
            var valid = DraftProfile();
            MustFail(() => ProfileComparison.Compare(null, valid));
            MustFail(() => ProfileComparison.Compare(valid, null));
            var invalid = valid.Copy(); invalid.Controller.Enabled = false; invalid.Controller.Map = null;
            MustFail(() => ProfileComparison.Compare(valid, invalid));
            invalid = valid.Copy(); invalid.Controller.LegionMap = new[] { 0, 0, 3, 0, 0, 0, 0 };
            MustFail(() => ProfileComparison.Compare(invalid, valid));
            invalid = valid.Copy(); invalid.Mode = 2;
            MustFail(() => ProfileComparison.Compare(valid, invalid));
            MustFail(() => ProfileComparison.Describe((ProfileComparisonResult)null));
        });
    }
}
