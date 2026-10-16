// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;

namespace LegionTweaks
{
    // Compare what the saved profile will do, rather than its JSON representation.
    // Values behind "Sin cambiar" / "Usar mando físico" are not applied.
    public static class ProfileDraft
    {
        public static bool Equivalent(ProfileDefinition left, ProfileDefinition right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            return string.Equals((left.Name ?? "").Trim(), (right.Name ?? "").Trim(), StringComparison.Ordinal)
                && left.Mode == right.Mode && (left.Mode != 255 || EqualValues(left.Limits, right.Limits))
                && left.BoostAC == right.BoostAC && left.BoostDC == right.BoostDC
                && left.EppAC == right.EppAC && left.EppDC == right.EppDC
                && left.WindowsMode == right.WindowsMode && EqualController(left.Controller, right.Controller);
        }

        private static bool EqualValues(int[] left, int[] right) => ReferenceEquals(left, right)
            || (left != null && right != null && left.SequenceEqual(right));

        private static bool EqualController(ControllerLayout left, ControllerLayout right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Enabled != right.Enabled) return false;
            return !left.Enabled || (EqualValues(left.Map, right.Map)
                && left.LeftDeadzone == right.LeftDeadzone && left.RightDeadzone == right.RightDeadzone
                && left.SwapSticks == right.SwapSticks && left.InvertLeftY == right.InvertLeftY
                && left.InvertRightY == right.InvertRightY && left.SwapTriggers == right.SwapTriggers
                && left.Rumble == right.Rumble);
        }

        public static string DescribeSaved(ProfileDefinition profile)
        {
            if (profile == null) return "Todavía no hay un perfil guardado seleccionado.";
            string mode = !profile.Mode.HasValue ? "Sin cambiar" : profile.Mode == 1 ? "Silencioso"
                : profile.Mode == 2 ? "Equilibrado" : profile.Mode == 3 ? "Rendimiento" : "Personalizado";
            if (profile.Mode == 255 && profile.Limits?.Length == 3)
                mode += " · SPL / SPPT / FPPT: " + string.Join(" / ", profile.Limits) + " W";
            string Boost(int? value) => !value.HasValue ? "Sin cambiar" : value == 0 ? "Desactivado" : "Activado";
            string Value(int? value) => value.HasValue ? value.Value.ToString() : "Sin cambiar";
            string windows = !profile.WindowsMode.HasValue ? "Sin cambiar" : profile.WindowsMode == 0 ? "Eficiencia"
                : profile.WindowsMode == 1 ? "Equilibrado" : "Rendimiento";
            string controller = profile.Controller == null ? "Conservar el mando actual"
                : !profile.Controller.Enabled ? "Usar mando físico" : "Xbox 360 virtual con su distribución guardada";
            return "Versión guardada de «" + profile.Name + "»\nModo Lenovo: " + mode
                + "\nBoost AC: " + Boost(profile.BoostAC) + " · DC: " + Boost(profile.BoostDC)
                + "\nEPP AC: " + Value(profile.EppAC) + " · DC: " + Value(profile.EppDC)
                + "\nModo Windows: " + windows + "\nMando: " + controller;
        }
    }
}
