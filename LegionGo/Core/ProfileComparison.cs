// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LegionTweaks
{
    public sealed class ProfileDifference
    {
        public string Setting { get; }
        public string Left { get; }
        public string Right { get; }
        internal ProfileDifference(string setting, string left, string right)
        { Setting = setting; Left = left; Right = right; }
    }

    public sealed class ProfileComparisonResult
    {
        public string LeftName { get; }
        public string RightName { get; }
        public IReadOnlyList<ProfileDifference> Differences { get; }
        internal ProfileComparisonResult(string leftName, string rightName, List<ProfileDifference> differences)
        { LeftName = leftName; RightName = rightName; Differences = differences.AsReadOnly(); }
    }

    // Compare saved intentions and stored layouts, never a live hardware snapshot.
    // Results retain only immutable strings, not references to editable profile arrays.
    public static class ProfileComparison
    {
        public static ProfileComparisonResult Compare(ProfileDefinition left, ProfileDefinition right)
        {
            var a = Snapshot(left, nameof(left)); var b = Snapshot(right, nameof(right));
            var differences = new List<ProfileDifference>();
            void Add(string setting, bool same, string av, string bv)
            { if (!same) differences.Add(new ProfileDifference(setting, av, bv)); }

            Add("Modo Lenovo", a.Mode == b.Mode, LenovoMode(a.Mode), LenovoMode(b.Mode));
            Add("SPL / SPPT / FPPT", a.Mode != 255 && b.Mode != 255 || ValuesEqual(a.Limits, b.Limits), Limits(a), Limits(b));
            Add("Boost con cargador (AC)", a.BoostAC == b.BoostAC, Boost(a.BoostAC), Boost(b.BoostAC));
            Add("Boost con batería (DC)", a.BoostDC == b.BoostDC, Boost(a.BoostDC), Boost(b.BoostDC));
            Add("EPP con cargador (AC)", a.EppAC == b.EppAC, Number(a.EppAC), Number(b.EppAC));
            Add("EPP con batería (DC)", a.EppDC == b.EppDC, Number(a.EppDC), Number(b.EppDC));
            Add("Modo Windows", a.WindowsMode == b.WindowsMode, WindowsMode(a.WindowsMode), WindowsMode(b.WindowsMode));

            var ac = a.Controller; var bc = b.Controller;
            Add("Mando", ac?.Enabled == bc?.Enabled && (ac == null) == (bc == null), ControllerMode(ac), ControllerMode(bc));
            if (ac != null || bc != null)
            {
                // Equal stored values do not become layout differences merely because
                // physical mode stops applying them. The mode row explains that change.
                for (int i = 0; i < ControllerLayout.Buttons.Length; i++)
                    Add("Botón " + ControllerLayout.Labels[i], ac != null && bc != null && ac.Map[i] == bc.Map[i],
                        LayoutValue(ac, ac == null ? null : Button(ac.Map[i])), LayoutValue(bc, bc == null ? null : Button(bc.Map[i])));
                for (int i = 0; i < 2; i++)
                    Add(ControllerLayout.LegionLabels[i], ac != null && bc != null && LegionTarget(ac, i) == LegionTarget(bc, i),
                        LayoutValue(ac, ac == null ? null : LegionButton(LegionTarget(ac, i))),
                        LayoutValue(bc, bc == null ? null : LegionButton(LegionTarget(bc, i))));
                Add("Zona muerta izquierda", ac != null && bc != null && ac.LeftDeadzone == bc.LeftDeadzone,
                    LayoutValue(ac, ac?.LeftDeadzone + " %"), LayoutValue(bc, bc?.LeftDeadzone + " %"));
                Add("Zona muerta derecha", ac != null && bc != null && ac.RightDeadzone == bc.RightDeadzone,
                    LayoutValue(ac, ac?.RightDeadzone + " %"), LayoutValue(bc, bc?.RightDeadzone + " %"));
                Add("Intercambiar sticks", ac != null && bc != null && ac.SwapSticks == bc.SwapSticks,
                    LayoutValue(ac, Yes(ac?.SwapSticks)), LayoutValue(bc, Yes(bc?.SwapSticks)));
                Add("Invertir eje Y izquierdo", ac != null && bc != null && ac.InvertLeftY == bc.InvertLeftY,
                    LayoutValue(ac, Yes(ac?.InvertLeftY)), LayoutValue(bc, Yes(bc?.InvertLeftY)));
                Add("Invertir eje Y derecho", ac != null && bc != null && ac.InvertRightY == bc.InvertRightY,
                    LayoutValue(ac, Yes(ac?.InvertRightY)), LayoutValue(bc, Yes(bc?.InvertRightY)));
                Add("Intercambiar gatillos", ac != null && bc != null && ac.SwapTriggers == bc.SwapTriggers,
                    LayoutValue(ac, Yes(ac?.SwapTriggers)), LayoutValue(bc, Yes(bc?.SwapTriggers)));
                Add("Vibración", ac != null && bc != null && ac.Rumble == bc.Rumble,
                    LayoutValue(ac, ac?.Rumble + " %"), LayoutValue(bc, bc?.Rumble + " %"));
            }
            return new ProfileComparisonResult(a.Name, b.Name, differences);
        }

        public static string Describe(ProfileDefinition left, ProfileDefinition right) => Describe(Compare(left, right));

        public static string Describe(ProfileComparisonResult comparison)
        {
            if (comparison == null) throw new ArgumentNullException(nameof(comparison));
            var text = new StringBuilder("A: «").Append(comparison.LeftName).Append("»\nB: «").Append(comparison.RightName).Append("»\n\n");
            text.Append("Se comparan ajustes guardados, sin leer ni cambiar el equipo. Los nombres e identificadores no cuentan como diferencias de ajustes.\n");
            text.Append("«Sin cambiar» conserva ese ajuste actual. «Conservar el mando actual» no inicia ni detiene el virtual. «Usar mando físico» detiene el virtual; su distribución queda guardada sin aplicarse.\n\n");
            if (comparison.Differences.Count == 0)
                text.Append("No hay diferencias en los ajustes comparados. Esto no confirma el estado del hardware ni la compatibilidad de un juego.");
            else
            {
                foreach (var difference in comparison.Differences)
                    text.Append(difference.Setting).Append("\nA: ").Append(difference.Left).Append("\nB: ").Append(difference.Right).Append("\n\n");
                text.Append("Las asignaciones Legion pueden estar reservadas por el atajo global de Game Bar. Esta comparación no comprueba esa preferencia ni la respuesta de los botones en hardware.");
            }
            return text.ToString();
        }

        private static ProfileDefinition Snapshot(ProfileDefinition profile, string argument)
        {
            if (profile == null) throw new ArgumentNullException(argument);
            var snapshot = profile.Copy(); snapshot.Validate(); return snapshot;
        }
        private static bool ValuesEqual(int[] a, int[] b) => ReferenceEquals(a, b) || a != null && b != null && a.SequenceEqual(b);
        private static string LenovoMode(int? value) => !value.HasValue ? "Sin cambiar" : value == 1 ? "Silencioso"
            : value == 2 ? "Equilibrado" : value == 3 ? "Rendimiento" : "Personalizado";
        private static string WindowsMode(int? value) => !value.HasValue ? "Sin cambiar" : value == 0 ? "Eficiencia"
            : value == 1 ? "Equilibrado" : "Rendimiento";
        private static string Limits(ProfileDefinition profile) => profile.Mode == 255
            ? string.Join(" / ", profile.Limits) + " W" : "No se aplican límites personalizados";
        private static string Boost(int? value) => !value.HasValue ? "Sin cambiar" : value == 0 ? "Desactivado" : "Activado";
        private static string Number(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "Sin cambiar";
        private static string ControllerMode(ControllerLayout layout) => layout == null ? "Conservar el mando actual"
            : layout.Enabled ? "Activar Xbox 360 virtual" : "Usar mando físico; detener el virtual";
        private static string LayoutValue(ControllerLayout layout, string value) => layout == null ? "Conservar el mando actual; sin distribución aplicada"
            : value + (layout.Enabled ? "" : " (guardado; no se aplica en modo físico)");
        private static string Yes(bool? value) => value == true ? "Sí" : "No";
        private static int LegionTarget(ControllerLayout layout, int index) => layout.LegionMap == null ? 0 : layout.LegionMap[index];
        private static string LegionButton(int target) => target == 0 ? "Sin asignación virtual" : Button(target);
        private static string Button(int target) => target == 0 ? "Sin función" : ControllerLayout.Labels[Array.IndexOf(ControllerLayout.Buttons, target)];
    }
}
