// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks.Widget
{
    internal enum ShellSection { Home, Power, Display, Controller, Profiles, Games, Settings }
    internal enum ShellFocusContext { Navigation, Content, TextEditor, ComboBox, Slider, Dialog }

    // Presentation state only. There is no transport, hardware operation or profile application here.
    internal sealed class ShellNavigation
    {
        internal static ShellSection[] Sections => (ShellSection[])Enum.GetValues(typeof(ShellSection));
        internal ShellSection Current { get; private set; } = ShellSection.Home;
        internal bool Select(ShellSection section)
        {
            if (!Enum.IsDefined(typeof(ShellSection), section) || section == Current) return false;
            Current = section; return true;
        }
        internal bool Select(string tag)
        {
            ShellSection section;
            return Enum.IsDefined(typeof(ShellSection), tag ?? "")
                && Enum.TryParse(tag, false, out section) && Select(section);
        }
        internal bool Move(int direction)
        {
            if (direction != -1 && direction != 1) return false;
            var sections = Sections;
            return Select(sections[((int)Current + direction + sections.Length) % sections.Length]);
        }
        internal static bool AllowsShortcut(ShellFocusContext context, bool busy, bool modal)
            => !busy && !modal && (context == ShellFocusContext.Navigation || context == ShellFocusContext.Content);
        internal static bool CompactNavigation(double width) => !(width >= 448);
        internal static int TileColumns(double width) => width >= 520 ? 3 : width >= 250 ? 2 : 1;
        internal static string Label(ShellSection section)
        {
            switch (section) {
                case ShellSection.Home: return "Inicio";
                case ShellSection.Power: return "Potencia";
                case ShellSection.Display: return "Pantalla";
                case ShellSection.Controller: return "Mando";
                case ShellSection.Profiles: return "Perfiles";
                case ShellSection.Games: return "Juegos";
                case ShellSection.Settings: return "Ajustes";
                default: throw new ArgumentException("Área desconocida.");
            }
        }
    }
}
